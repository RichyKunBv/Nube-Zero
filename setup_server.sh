#!/bin/bash
# Nube-Zero Server - Instalador Oficial para Raspberry Pi (Linux ARM)
# Ejecutar con permisos de superusuario (sudo)

set -e

REPO_OWNER="RichyKunBv"
REPO_NAME="Nube-Zero"
INSTALL_DIR="/opt/nubezero"
BIN_DIR="$INSTALL_DIR/bin"
SERVICE_FILE="/etc/systemd/system/nubezero.service"

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

function print_msg() {
  echo -e "\n${GREEN}[+] $1${NC}"
}

function print_warn() {
  echo -e "\n${YELLOW}[!] $1${NC}"
}

function install_dependencies() {
  print_msg "Verificando e instalando dependencias (curl, unzip, mono)..."
  
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
  apt-get install -y curl unzip sqlite3
  
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
  ASSET_URL=$(curl -s "https://api.github.com/repos/$REPO_OWNER/$REPO_NAME/releases/latest" | grep '"browser_download_url":' | grep 'NubeZero-Server.zip' | cut -d '"' -f 4)
  
  if [ -z "$ASSET_URL" ]; then
    echo -e "${RED}ERROR: No se pudo encontrar 'NubeZero-Server.zip' en la última release de GitHub.${NC}"
    echo -e "${YELLOW}Asegúrate de que GitHub Actions ya haya terminado de compilar la nueva versión.${NC}"
    exit 1
  fi
  
  TMP_DIR=$(mktemp -d)
  cd "$TMP_DIR"
  
  curl -L "$ASSET_URL" -o NubeZero-Server.zip
  unzip -q NubeZero-Server.zip -d extracted
  
  print_msg "Instalando binarios..."
  mkdir -p "$BIN_DIR"
  
  # Si el zip contiene una carpeta publish_out o similar, ajustamos
  if [ -d "extracted/publish_out" ]; then
      cp -r extracted/publish_out/* "$BIN_DIR/"
  else
      cp -r extracted/* "$BIN_DIR/"
  fi
  
  # Limpieza
  cd /
  rm -rf "$TMP_DIR"
}

function setup_systemd() {
  local port=$1
  print_msg "Configurando servicio Systemd (Puerto $port)..."
  
  cat <<EOF > "$SERVICE_FILE"
[Unit]
Description=Nube-Zero Server
After=network.target

[Service]
Type=simple
User=root
WorkingDirectory=$INSTALL_DIR
ExecStart=/usr/bin/mono $BIN_DIR/NubeZero.Server.exe --port $port
Restart=always
RestartSec=10

[Install]
WantedBy=multi-user.target
EOF

  systemctl daemon-reload
  systemctl enable nubezero
  systemctl start nubezero
  
  print_msg "¡Nube-Zero instalado y ejecutándose exitosamente!"
  echo "Puedes ver los logs con: journalctl -u nubezero -f"
}

function action_install() {
  echo -ne "Introduce el puerto en el que deseas que corra el servidor (Por defecto 8080): "
  read PORT_INPUT
  if [ -z "$PORT_INPUT" ]; then
    PORT_INPUT=8080
  fi
  
  install_dependencies
  fetch_and_install
  setup_systemd "$PORT_INPUT"
}

function action_update() {
  print_msg "Iniciando actualización de Nube-Zero..."
  
  if [ ! -f "$SERVICE_FILE" ]; then
    echo -e "${RED}No se encontró el servicio Nube-Zero. Por favor usa la opción de Instalar.${NC}"
    exit 1
  fi
  
  CURRENT_PORT=$(grep "ExecStart" "$SERVICE_FILE" | grep -oP '(?<=--port )\d+')
  if [ -z "$CURRENT_PORT" ]; then
    CURRENT_PORT=8080
  fi
  
  systemctl stop nubezero
  
  fetch_and_install
  
  systemctl start nubezero
  print_msg "¡Nube-Zero actualizado a la última versión (Puerto $CURRENT_PORT)!"
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

echo "Elige una opción:"
echo "  1) Instalar Servidor Nube-Zero"
echo "  2) Actualizar Servidor (Última Release)"
echo "  3) Desinstalar Servidor"
echo "  4) Salir"
echo -ne "Opción: "
read OPTION

case $OPTION in
  1) action_install ;;
  2) action_update ;;
  3) action_uninstall ;;
  4) exit 0 ;;
  *) echo "Opción no válida."; exit 1 ;;
esac
