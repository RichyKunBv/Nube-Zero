#!/bin/bash
# Nube-Zero Server - Instalador Oficial para Raspberry Pi (Linux ARM)
# Ejecutar con permisos de superusuario (sudo)

set -e

REPO_OWNER="RichyKunBv"
REPO_NAME="Nube-Zero"
INSTALL_DIR="/opt/nubezero"
BIN_DIR="$INSTALL_DIR/bin"
SERVICE_FILE="/etc/systemd/system/nubezero.service"
TLS_SERVICE_FILE="/etc/systemd/system/nubezero-tls.service"
CONFIG_DIR="/etc/nubezero"
TLS_DIR="$CONFIG_DIR/tls"
SERVER_ENV_FILE="$CONFIG_DIR/server.env"
NUBEZERO_USER="nubezero"

# Colores
GREEN='\033[0;32m'
RED='\033[0;31m'
YELLOW='\033[1;33m'
NC='\033[0m'

echo -e "${GREEN}==========================================${NC}"
echo -e "${GREEN}    Instalador de Nube-Zero (Server)      ${NC}"
echo -e "${GREEN}==========================================${NC}"

if [ "$EUID" -ne 0 ]; then
  echo -e "${RED}ERROR: Por favor, ejecuta este script como root (usando sudo).${NC}"
  exit 1
fi

# Autogestión de permisos (Si el sistema está en Solo Lectura)
mount -o remount,rw / 2>/dev/null || true
mount -o remount,rw /boot/firmware 2>/dev/null || true

function print_msg() {
  echo -e "\n${GREEN}[+] $1${NC}"
}

function print_warn() {
  echo -e "\n${YELLOW}[!] $1${NC}"
}

function check_multiple_installations() {
  print_msg "Verificando si existen múltiples instalaciones de Nube-Zero..."

  FOUND_EXTRAS=$(find /home /mnt /media /usr/local -type f -name "NubeZero.Server.exe" 2>/dev/null)
  
  if [ -n "$FOUND_EXTRAS" ]; then
    print_warn "Se detectaron otras instalaciones o copias. No se eliminará ningún archivo automáticamente:"
    echo "$FOUND_EXTRAS" | while read -r line; do
        echo -e "${RED}- $line${NC}"
    done
  else
    echo "Todo en orden (Instalación única detectada)."
  fi
}

function install_dependencies() {
  print_msg "Verificando e instalando dependencias (curl, unzip, mono, openssl, stunnel)..."
  
  # Limpiar repositorios problemáticos de Mono si fueron agregados por versiones anteriores o tutoriales viejos
  print_warn "Buscando y deshabilitando repositorios rotos de Mono en el sistema..."
  grep -rl "download.mono-project.com" /etc/apt/ | while read -r file; do
      if [ -f "$file" ]; then
          echo "Comentando repo inválido en: $file"
          sed -i 's/^deb .*download.mono-project.com/# &/' "$file"
      fi
  done
  
  rm -f /etc/apt/keyrings/mono-official-archive-keyring.gpg || true
  
  apt-get update -y
  apt-get install -y curl unzip openssl stunnel4
  
  if ! command -v mono &> /dev/null; then
    print_warn "Mono no encontrado. Intentando instalar desde los repositorios oficiales de tu sistema..."
    apt-get install -y mono-complete || apt-get install -y mono-runtime
    
    if ! command -v mono &> /dev/null; then
        echo -e "${RED}ERROR: No se pudo instalar mono desde ninguna fuente.${NC}"
        echo -e "${RED}Por favor instala Mono manualmente en tu Raspberry Pi e intenta de nuevo.${NC}"
        exit 1
    fi
  else
    echo "Mono ya está instalado."
  fi
}

function fetch_and_install() {
  print_msg "Descargando servidor Nube-Zero de la última Release..."
  
  # Obtenemos la URL del NubeZero-Server.zip
  local release_response
  release_response=$(curl -fsSL --show-error "https://api.github.com/repos/$REPO_OWNER/$REPO_NAME/releases/latest")
  ASSET_URL=$(printf '%s\n' "$release_response" | grep '"browser_download_url":' | grep 'NubeZero-Server.zip' | cut -d '"' -f 4)
  
  if [ -z "$ASSET_URL" ]; then
    echo -e "${RED}ERROR: No se pudo encontrar 'NubeZero-Server.zip' en la última release de GitHub.${NC}"
    echo -e "${YELLOW}Asegúrate de que GitHub Actions ya haya terminado de compilar la nueva versión.${NC}"
    exit 1
  fi
  
  TMP_DIR=$(mktemp -d)
  cd "$TMP_DIR"
  
  curl -fSL --show-error "$ASSET_URL" -o NubeZero-Server.zip
  unzip -q NubeZero-Server.zip -d extracted
  
  print_msg "Instalando binarios..."
  mkdir -p "$BIN_DIR"
  
  # Si el zip contiene una carpeta publish_out o similar, ajustamos
  if [ -d "extracted/publish_out" ]; then
      cp -r extracted/publish_out/* "$BIN_DIR/"
  else
      cp -r extracted/* "$BIN_DIR/"
  fi
  
  # Instalar CLI
  if [ -f "extracted/cli/nubezero.sh" ]; then
      print_msg "Instalando herramienta de línea de comandos (CLI)..."
      cp extracted/cli/nubezero.sh /usr/local/bin/nubezero
      chmod +x /usr/local/bin/nubezero
  elif [ -f "$BIN_DIR/cli/nubezero.sh" ]; then
      print_msg "Instalando herramienta de línea de comandos (CLI)..."
      cp "$BIN_DIR/cli/nubezero.sh" /usr/local/bin/nubezero
      chmod +x /usr/local/bin/nubezero
  else
      print_msg "Descargando herramienta CLI nubezero..."
      curl -sL https://raw.githubusercontent.com/$REPO_OWNER/$REPO_NAME/main/cli/nubezero.sh -o /usr/local/bin/nubezero 2>/dev/null || true
      chmod +x /usr/local/bin/nubezero 2>/dev/null || true
  fi
  
  # Limpieza
  cd /
  rm -rf "$TMP_DIR"
}

function setup_systemd() {
  local port=$1
  local name=$2
  local storage_path=$3
  local storage_argument=""
  if [ -z "$name" ]; then
    name=$(hostname)
  fi
  if ! [[ "$name" =~ ^[a-zA-Z0-9._\ -]{1,64}$ ]]; then
    echo -e "${RED}ERROR: El nombre del servidor solo puede contener letras, números, espacios, puntos, guiones y guiones bajos.${NC}"
    return 1
  fi
  if [ -n "$storage_path" ]; then
    if [[ "$storage_path" != /* || "$storage_path" == *'"'* || "$storage_path" == *'\'* || "$storage_path" == *'%'* || "$storage_path" == *$'\n'* ]]; then
      echo -e "${RED}ERROR: La ruta de almacenamiento conservada no es segura para systemd.${NC}"
      return 1
    fi
    storage_argument=" --storage \"$storage_path\""
  fi
  print_msg "Configurando servidor local y proxy HTTPS (Puerto público $port, Nombre: $name)..."

  if ! id -u "$NUBEZERO_USER" >/dev/null 2>&1; then
    useradd --system --home-dir "$INSTALL_DIR" --no-create-home --shell /usr/sbin/nologin "$NUBEZERO_USER"
  fi

  install -d -o "$NUBEZERO_USER" -g "$NUBEZERO_USER" -m 0750 "$INSTALL_DIR"
  install -d -o "$NUBEZERO_USER" -g "$NUBEZERO_USER" -m 0750 "$INSTALL_DIR/Storage"
  if [ -f "$INSTALL_DIR/database.json" ]; then
    chown "$NUBEZERO_USER:$NUBEZERO_USER" "$INSTALL_DIR/database.json"
    chmod 0600 "$INSTALL_DIR/database.json"
  fi
  if [ -d "$INSTALL_DIR/Storage" ]; then
    find "$INSTALL_DIR/Storage" -type d -exec chown "$NUBEZERO_USER:$NUBEZERO_USER" {} +
  fi

  install -d -o root -g root -m 0750 "$CONFIG_DIR"
  install -d -o root -g root -m 0700 "$TLS_DIR"
  local encryption_key=""
  if [ ! -f "$SERVER_ENV_FILE" ] || ! grep -q '^NUBEZERO_FILE_ENCRYPTION_KEY=' "$SERVER_ENV_FILE"; then
    encryption_key=$(openssl rand -base64 32)
    printf 'NUBEZERO_FILE_ENCRYPTION_KEY=%s\n' "$encryption_key" >> "$SERVER_ENV_FILE"
    echo "Clave de cifrado del servidor (guárdala ahora en un gestor de contraseñas; no se volverá a mostrar):"
    echo "$encryption_key"
  fi

  if { [ ! -f "$INSTALL_DIR/database.json" ] || ! grep -qi '"Username"[[:space:]]*:[[:space:]]*"admin"' "$INSTALL_DIR/database.json"; } \
    && ! grep -q '^NUBEZERO_INITIAL_ADMIN_PASSWORD_BASE64=' "$SERVER_ENV_FILE"; then
    local admin_password=""
    local admin_password_confirmation=""
    while [ "${#admin_password}" -lt 12 ] || [ "$admin_password" != "$admin_password_confirmation" ]; do
      read -r -s -p "Introduce la contraseña inicial de admin (mínimo 12 caracteres): " admin_password
      echo
      if [ "${#admin_password}" -lt 12 ]; then
        print_warn "La contraseña debe tener al menos 12 caracteres."
        admin_password=""
        continue
      fi
      read -r -s -p "Confirma la contraseña inicial: " admin_password_confirmation
      echo
      if [ "$admin_password" != "$admin_password_confirmation" ]; then
        print_warn "Las contraseñas no coinciden."
        admin_password=""
      fi
    done
    local encoded_admin_password
    encoded_admin_password=$(printf '%s' "$admin_password" | base64 | tr -d '\n')
    printf 'NUBEZERO_INITIAL_ADMIN_PASSWORD_BASE64=%s\n' "$encoded_admin_password" >> "$SERVER_ENV_FILE"
    unset admin_password admin_password_confirmation encoded_admin_password
  fi
  chown root:root "$SERVER_ENV_FILE"
  chmod 0600 "$SERVER_ENV_FILE"

  if [ ! -f "$TLS_DIR/server.crt" ] || [ ! -f "$TLS_DIR/server.key" ]; then
    openssl req -x509 -newkey rsa:2048 -sha256 -nodes -days 3650 \
      -keyout "$TLS_DIR/server.key" \
      -out "$TLS_DIR/server.crt" \
      -subj "/CN=Nube-Zero local server" \
      -addext "subjectAltName=DNS:localhost,IP:127.0.0.1"
    chmod 0600 "$TLS_DIR/server.key"
    chmod 0644 "$TLS_DIR/server.crt"
  fi
  cat "$TLS_DIR/server.crt" "$TLS_DIR/server.key" > "$TLS_DIR/server.pem"
  chown root:"$NUBEZERO_USER" "$TLS_DIR/server.pem"
  chmod 0640 "$TLS_DIR/server.pem"

  local fingerprint
  fingerprint=$(openssl x509 -in "$TLS_DIR/server.crt" -noout -fingerprint -sha256 | cut -d= -f2)
  cat <<EOF > "$CONFIG_DIR/stunnel.conf"
foreground = yes
setuid = $NUBEZERO_USER
setgid = $NUBEZERO_USER
cert = $TLS_DIR/server.pem
sslVersionMin = TLSv1.2

[nubezero]
accept = 0.0.0.0:$port
connect = 127.0.0.1:8081
EOF
  chown root:"$NUBEZERO_USER" "$CONFIG_DIR/stunnel.conf"
  chmod 0640 "$CONFIG_DIR/stunnel.conf"
  
  cat <<EOF > "$SERVICE_FILE"
[Unit]
Description=Nube-Zero Server
After=network.target

[Service]
Type=simple
User=$NUBEZERO_USER
Group=$NUBEZERO_USER
EnvironmentFile=$SERVER_ENV_FILE
WorkingDirectory=$INSTALL_DIR
ExecStart=/usr/bin/mono $BIN_DIR/NubeZero.Server.exe --port 8081 --public-port $port --name "$name"$storage_argument
Restart=always
RestartSec=10
UMask=0077
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=$INSTALL_DIR
CapabilityBoundingSet=

[Install]
WantedBy=multi-user.target
EOF
  cat <<EOF > "$TLS_SERVICE_FILE"
[Unit]
Description=Nube-Zero HTTPS/TLS proxy
After=network.target nubezero.service
Requires=nubezero.service

[Service]
Type=simple
ExecStart=/usr/bin/stunnel4 $CONFIG_DIR/stunnel.conf
Restart=always
RestartSec=5
NoNewPrivileges=true

[Install]
WantedBy=multi-user.target
EOF

  systemctl daemon-reload
  systemctl enable nubezero
  systemctl restart nubezero
  if grep -q '^NUBEZERO_INITIAL_ADMIN_PASSWORD_BASE64=' "$SERVER_ENV_FILE"; then
    local attempt
    for attempt in $(seq 1 60); do
      if grep -qi '"Username"[[:space:]]*:[[:space:]]*"admin"' "$INSTALL_DIR/database.json" 2>/dev/null; then
        break
      fi
      sleep 1
    done
    if grep -qi '"Username"[[:space:]]*:[[:space:]]*"admin"' "$INSTALL_DIR/database.json" 2>/dev/null; then
      sed -i '/^NUBEZERO_INITIAL_ADMIN_PASSWORD_BASE64=/d' "$SERVER_ENV_FILE"
      chown root:root "$SERVER_ENV_FILE"
      chmod 0600 "$SERVER_ENV_FILE"
      systemctl restart nubezero
      print_msg "Se eliminó la contraseña inicial temporal de la configuración del servicio."
    else
      print_warn "No se pudo confirmar la creación del usuario inicial. La contraseña temporal permanece protegida en $SERVER_ENV_FILE."
    fi
  fi
  systemctl enable nubezero-tls
  systemctl restart nubezero-tls
  
  print_msg "¡Nube-Zero instalado y ejecutándose exitosamente!"
  echo "Huella SHA-256 del certificado (verifícala por SSH antes de introducirla en los clientes):"
  echo "$fingerprint"
  echo "Puedes ver los logs con: journalctl -u nubezero -f"
}

function action_install() {
  echo -ne "Introduce el nombre para identificar este servidor en la red (Por defecto $(hostname)): "
  read NAME_INPUT
  if [ -z "$NAME_INPUT" ]; then
    NAME_INPUT=$(hostname)
  fi

  echo -ne "Introduce el puerto en el que deseas que corra el servidor (Por defecto 8080): "
  read PORT_INPUT
  if [ -z "$PORT_INPUT" ]; then
    PORT_INPUT=8080
  fi
  if ! [[ "$PORT_INPUT" =~ ^[0-9]+$ ]] || [ "$PORT_INPUT" -lt 1024 ] || [ "$PORT_INPUT" -gt 65535 ]; then
    echo -e "${RED}ERROR: El puerto debe ser un número entre 1024 y 65535.${NC}"
    exit 1
  fi
  
  install_dependencies
  fetch_and_install
  setup_systemd "$PORT_INPUT" "$NAME_INPUT" ""
  
  check_multiple_installations
}

function action_update() {
  print_msg "Iniciando actualización de Nube-Zero..."
  
  if [ ! -f "$SERVICE_FILE" ]; then
    echo -e "${RED}No se encontró el servicio Nube-Zero. Por favor usa la opción de Instalar.${NC}"
    exit 1
  fi
  
  local exec_start
  exec_start=$(sed -n 's/^ExecStart=//p' "$SERVICE_FILE" | head -n 1)
  if [ -z "$exec_start" ]; then
    echo -e "${RED}No se encontró una línea ExecStart válida en $SERVICE_FILE.${NC}"
    exit 1
  fi

  CURRENT_PORT=$(printf '%s\n' "$exec_start" | sed -nE 's/.*--public-port ([0-9]+).*/\1/p')
  if [ -z "$CURRENT_PORT" ]; then
    CURRENT_PORT=8080
  fi
  
  CURRENT_STORAGE=$(printf '%s\n' "$exec_start" | sed -nE 's/.*--storage ([^[:space:]]+).*/\1/p')
  CURRENT_NAME=$(printf '%s\n' "$exec_start" | sed -nE 's/.*--name "([^"]*)".*/\1/p')
  if [ -z "$CURRENT_NAME" ]; then
    CURRENT_NAME=$(printf '%s\n' "$exec_start" | sed -nE 's/.*--name ([^[:space:]]+).*/\1/p')
  fi
  
  # Forzar que el sistema siempre apunte a la ruta de instalación oficial
  fetch_and_install
  setup_systemd "$CURRENT_PORT" "$CURRENT_NAME" "$CURRENT_STORAGE"
  print_msg "¡Nube-Zero actualizado a la última versión (Puerto $CURRENT_PORT)!"
  
  check_multiple_installations
}

function action_uninstall() {
  print_warn "Iniciando desinstalación de Nube-Zero..."
  
  systemctl stop nubezero || true
  systemctl disable nubezero || true
  rm -f "$SERVICE_FILE"
  systemctl daemon-reload
  
  echo -ne "¿Deseas eliminar también tu base de datos y archivos subidos? (Ubicados en $INSTALL_DIR/Storage) [y/N]: "
  read RM_DB
  if [[ "$RM_DB" == "y" || "$RM_DB" == "Y" ]]; then
    rm -rf "$INSTALL_DIR"
    print_msg "Archivos y Base de Datos eliminados."
  else
    rm -rf "$BIN_DIR"
    print_msg "Binarios eliminados. La base de datos y archivos se han conservado en $INSTALL_DIR/Storage."
  fi
  
  print_msg "Desinstalación completada."
}

function action_update_cli() {
  print_msg "Actualizando solo la herramienta de línea de comandos (CLI nubezero)..."
  
  mount -o remount,rw / 2>/dev/null || true
  mount -o remount,rw /boot/firmware 2>/dev/null || true

  local tmp_cli=$(mktemp)
  local download_url="https://raw.githubusercontent.com/$REPO_OWNER/$REPO_NAME/main/cli/nubezero.sh"

  if wget -q "$download_url" -O "$tmp_cli" 2>/dev/null || curl -sL "$download_url" -o "$tmp_cli" 2>/dev/null; then
    if bash -n "$tmp_cli"; then
      cp "$tmp_cli" /usr/local/bin/nubezero
      chmod +x /usr/local/bin/nubezero
      rm -f "$tmp_cli"
      print_msg "¡Herramienta CLI (/usr/local/bin/nubezero) actualizada con éxito desde main!"
    else
      echo -e "${RED}Error: El script descargado contiene errores de sintaxis.${NC}"
      rm -f "$tmp_cli"
      exit 1
    fi
  else
    echo -e "${RED}Error: No se pudo descargar el script CLI desde GitHub.${NC}"
    rm -f "$tmp_cli"
    exit 1
  fi
}

# Ejecución por argumentos o interactiva
if [ "$1" == "update" ]; then
  action_update
elif [ "$1" == "update-cli" ]; then
  action_update_cli
elif [ "$1" == "install" ]; then
  action_install
elif [ "$1" == "uninstall" ]; then
  action_uninstall
else
  echo "Elige una opción:"
  echo "  1) Instalar Servidor Nube-Zero"
  echo "  2) Actualizar Servidor (Última Release)"
  echo "  3) Actualizar solo herramienta CLI (desde main)"
  echo "  4) Desinstalar Servidor"
  echo "  5) Salir"
  echo -ne "Opción: "
  read OPTION

  case $OPTION in
    1) action_install ;;
    2) action_update ;;
    3) action_update_cli ;;
    4) action_uninstall ;;
    5) exit 0 ;;
    *) echo "Opción no válida."; exit 1 ;;
  esac
fi
