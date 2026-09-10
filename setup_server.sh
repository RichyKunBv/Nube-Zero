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
  print_msg "Verificando e instalando dependencias (curl, unzip, mono, msbuild)..."
  
  # Limpiar repositorios problemáticos de Mono si fueron agregados por versiones anteriores o tutoriales viejos
  print_warn "Buscando y deshabilitando repositorios rotos de Mono en el sistema..."
  grep -rl "download.mono-project.com" /etc/apt/ | while read -r file; do
      if [ -f "$file" ]; then
          echo "Comentando repo inválido en: $file"
          sed -i 's/^deb .*download.mono-project.com/# &/' "$file"
      fi
  done
  
  rm -f /etc/apt/keyrings/mono-official-archive-keyring.gpg
  
  apt-get update -y
  apt-get install -y curl unzip
  
  if ! command -v mono &> /dev/null || ! command -v msbuild &> /dev/null; then
    print_warn "Mono o MSBuild no encontrados. Intentando instalar desde los repositorios oficiales de tu sistema..."
    
    # Mono y MSBuild suelen estar disponibles en los repositorios por defecto en Debian 11+ / Raspbian
    apt-get install -y mono-complete || apt-get install -y mono-devel
    
    if ! command -v msbuild &> /dev/null; then
        apt-get install -y msbuild || true
    fi
    
    if ! command -v msbuild &> /dev/null; then
        apt-get install -y mono-msbuild || true
    fi
    
    if ! command -v msbuild &> /dev/null; then
        print_warn "No se encontró MSBuild en los repositorios locales."
        print_warn "Forzando instalación desde el repositorio oficial de Mono (saltando firma GPG obsoleta)..."
        
        echo "deb [trusted=yes] https://download.mono-project.com/repo/debian stable-buster main" > /etc/apt/sources.list.d/mono-official-stable.list
        apt-get update -y --allow-insecure-repositories || true
        apt-get install -y --allow-unauthenticated mono-complete msbuild
    fi
    
    if ! command -v msbuild &> /dev/null; then
        echo -e "${RED}ERROR: No se pudo instalar msbuild desde ninguna fuente.${NC}"
        echo -e "${RED}Por favor instala Mono y MSBuild manualmente en tu Raspberry Pi e intenta de nuevo.${NC}"
        exit 1
    fi
  else
    echo "Mono y MSBuild ya están instalados."
  fi
}

function manage_swap() {
  # Verificar RAM libre en MB
  FREE_RAM=$(free -m | awk '/^Mem:/{print $4}')
  print_msg "Memoria RAM libre detectada: ${FREE_RAM} MB"
  
  if [ "$FREE_RAM" -lt 1000 ]; then
    print_warn "Memoria insuficiente para compilar seguro. Creando un Swap temporal de 1GB..."
    if [ -f /swapfile_nubezero ]; then
        swapoff /swapfile_nubezero || true
        rm -f /swapfile_nubezero
    fi
    fallocate -l 1G /swapfile_nubezero || dd if=/dev/zero of=/swapfile_nubezero bs=1M count=1024
    chmod 600 /swapfile_nubezero
    mkswap /swapfile_nubezero
    swapon /swapfile_nubezero
    echo "Swap activado."
    SWAP_CREATED=1
  else
    SWAP_CREATED=0
  fi
}

function remove_swap() {
  if [ "$SWAP_CREATED" -eq 1 ]; then
    print_msg "Eliminando Swap temporal..."
    swapoff /swapfile_nubezero
    rm -f /swapfile_nubezero
    echo "Swap eliminado."
  fi
}

function fetch_and_compile() {
  print_msg "Descargando código fuente de la última Release..."
  
  LATEST_RELEASE_URL=$(curl -s "https://api.github.com/repos/$REPO_OWNER/$REPO_NAME/releases/latest" | grep '"zipball_url":' | cut -d '"' -f 4)
  
  if [ -z "$LATEST_RELEASE_URL" ]; then
    echo -e "${RED}ERROR: No se pudo obtener la URL de la última release. Verifica tu conexión a internet o el límite de API de GitHub.${NC}"
    exit 1
  fi
  
  TMP_DIR=$(mktemp -d)
  cd "$TMP_DIR"
  
  curl -L "$LATEST_RELEASE_URL" -o source.zip
  unzip -q source.zip
  
  # El zip de GitHub descomprime en una carpeta con el hash del commit
  EXTRACTED_DIR=$(ls -d */ | head -n 1)
  cd "$EXTRACTED_DIR"
  
  print_msg "Limpiando proyectos innecesarios (Desktop, Mobile)..."
  rm -rf src/desktop
  rm -rf src/mobile
  
  print_msg "Iniciando compilación local (esto puede tardar varios minutos en la Raspberry)..."
  
  # Usar msbuild para compilar
  if ! msbuild src/server/NubeZero.Server.csproj /p:Configuration=Release /p:TargetFramework=net472; then
    echo -e "${RED}================================================================${NC}"
    echo -e "${RED}ERROR CRÍTICO: La compilación ha fallado.${NC}"
    echo -e "${RED}Revisa el registro arriba para ver los detalles del error.${NC}"
    echo -e "${RED}================================================================${NC}"
    remove_swap
    exit 1
  fi
  
  print_msg "Compilación exitosa. Copiando binarios..."
  mkdir -p "$BIN_DIR"
  cp -r src/server/bin/Release/net472/* "$BIN_DIR/"
  
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
ExecStart=/usr/bin/mono $BIN_DIR/NubeZero.exe --port $port
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
  manage_swap
  fetch_and_compile
  remove_swap
  setup_systemd "$PORT_INPUT"
}

function action_update() {
  print_msg "Iniciando actualización de Nube-Zero..."
  
  if [ ! -f "$SERVICE_FILE" ]; then
    echo -e "${RED}No se encontró el servicio Nube-Zero. Por favor usa la opción de Instalar.${NC}"
    exit 1
  fi
  
  # Extraer el puerto configurado actualmente
  CURRENT_PORT=$(grep "ExecStart" "$SERVICE_FILE" | grep -oP '(?<=--port )\d+')
  if [ -z "$CURRENT_PORT" ]; then
    CURRENT_PORT=8080
  fi
  
  systemctl stop nubezero
  
  manage_swap
  fetch_and_compile
  remove_swap
  
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
