#!/bin/bash
# Nube-Zero CLI - Herramienta de Configuración y Gestión

SERVICE_NAME="nubezero.service"
SERVICE_FILE="/etc/systemd/system/nubezero.service"
INSTALL_DIR="/opt/nubezero"

# Colores
GREEN='\033[0;32m'
RED='\033[0;31m'
YELLOW='\033[1;33m'
CYAN='\033[0;36m'
NC='\033[0m'

if [ "$EUID" -ne 0 ]; then
  echo -e "${RED}Por favor ejecuta este comando con sudo: sudo nubezero $1${NC}"
  exit 1
fi

function print_header() {
  echo -e "${CYAN}==========================================${NC}"
  echo -e "${CYAN}             ☁️ Nube-Zero CLI             ${NC}"
  echo -e "${CYAN}==========================================${NC}"
}

function run_config() {
  print_header
  echo -e "${YELLOW}--- Asistente de Configuración ---${NC}"
  
  # 1. Almacenamiento
  echo -e "\n${CYAN}1. Almacenamiento Externo (USB / OTG)${NC}"
  echo -e "¿Deseas utilizar un dispositivo de almacenamiento externo? [y/N]"
  read USE_EXT
  if [[ "$USE_EXT" == "y" || "$USE_EXT" == "Y" ]]; then
      echo -e "\nDispositivos disponibles:"
      lsblk -o NAME,SIZE,TYPE,MOUNTPOINT | grep -v 'loop' | grep -v 'rom'
      
      echo -e "\nIntroduce el nombre de la partición a usar (Ej: sda1 o sdb1):"
      read DISK_NAME
      
      if [ -b "/dev/$DISK_NAME" ]; then
          echo -e "${RED}¡ADVERTENCIA! ¿Deseas formatear /dev/$DISK_NAME a ext4? Esto borrará TODOS los datos de esa partición. [y/N]${NC}"
          read FORMAT_DISK
          if [[ "$FORMAT_DISK" == "y" || "$FORMAT_DISK" == "Y" ]]; then
              echo "Formateando /dev/$DISK_NAME a ext4..."
              mkfs.ext4 -F "/dev/$DISK_NAME"
          fi
          
          MOUNT_DIR="/mnt/nubezero_usb"
          mkdir -p "$MOUNT_DIR"
          
          # Verificar si ya existe en fstab
          if ! grep -q "/dev/$DISK_NAME" /etc/fstab; then
              echo "/dev/$DISK_NAME $MOUNT_DIR ext4 defaults,noatime 0 2" >> /etc/fstab
          fi
          
          mount -a
          chown -R root:root "$MOUNT_DIR"
          
          # Actualizar systemd manteniendo el puerto actual
          CURRENT_PORT=$(grep "ExecStart" "$SERVICE_FILE" | grep -oP '(?<=--port )\d+')
          if [ -z "$CURRENT_PORT" ]; then CURRENT_PORT=8080; fi
          
          sed -i "s|ExecStart=.*|ExecStart=/usr/bin/mono $INSTALL_DIR/bin/NubeZero.Server.exe --port $CURRENT_PORT --storage $MOUNT_DIR|g" "$SERVICE_FILE"
          echo -e "${GREEN}Almacenamiento configurado en $MOUNT_DIR${NC}"
      else
          echo -e "${RED}Dispositivo /dev/$DISK_NAME no encontrado.${NC}"
      fi
  else
      echo -e "\n¿Deseas desvincular un almacenamiento externo y volver a la MicroSD local? [y/N]"
      read UNLINK_EXT
      if [[ "$UNLINK_EXT" == "y" || "$UNLINK_EXT" == "Y" ]]; then
          echo -e "${YELLOW}Aviso: Los archivos del USB no se copiarán a la MicroSD para evitar saturarla.${NC}"
          echo -e "¿Borrar la base de datos local anterior para iniciar limpio? [y/N]"
          read DEL_LOCAL
          if [[ "$DEL_LOCAL" == "y" || "$DEL_LOCAL" == "Y" ]]; then
              rm -f "$INSTALL_DIR/bin/database.json"
          fi
          
          CURRENT_PORT=$(grep "ExecStart" "$SERVICE_FILE" | grep -oP '(?<=--port )\d+')
          if [ -z "$CURRENT_PORT" ]; then CURRENT_PORT=8080; fi
          
          sed -i "s|ExecStart=.*|ExecStart=/usr/bin/mono $INSTALL_DIR/bin/NubeZero.Server.exe --port $CURRENT_PORT|g" "$SERVICE_FILE"
          echo -e "${GREEN}Se ha vuelto al almacenamiento de la MicroSD.${NC}"
      fi
  fi
  
  # 2. Rendimiento
  echo -e "\n${CYAN}2. Optimización del Sistema${NC}"
  echo -e "¿Será Nube-Zero el uso exclusivo de esta Raspberry? (Desactivará Bluetooth y otros servicios para ahorrar RAM) [y/N]"
  read EXCLUSIVE_USE
  if [[ "$EXCLUSIVE_USE" == "y" || "$EXCLUSIVE_USE" == "Y" ]]; then
      systemctl disable bluetooth hciuart triggerhappy avahi-daemon 2>/dev/null || true
      systemctl stop bluetooth hciuart triggerhappy avahi-daemon 2>/dev/null || true
      echo -e "${GREEN}Servicios inútiles desactivados.${NC}"
  fi
  
  systemctl daemon-reload
  systemctl restart nubezero
  echo -e "\n${GREEN}¡Configuración completada y servicio reiniciado!${NC}"
}

function run_update() {
  print_header
  echo -e "${YELLOW}Actualizando servidor Nube-Zero...${NC}"
  
  TMP_SCRIPT=$(mktemp)
  curl -sL https://raw.githubusercontent.com/RichyKunBv/Nube-Zero/main/setup_server.sh -o "$TMP_SCRIPT"
  chmod +x "$TMP_SCRIPT"
  
  # Ejecutar el instalador en modo update
  "$TMP_SCRIPT" update
  rm -f "$TMP_SCRIPT"
}

case "$1" in
  -c|config)
    run_config
    ;;
  -u|update)
    run_update
    ;;
  start)
    systemctl start nubezero
    echo -e "${GREEN}Servicio iniciado.${NC}"
    ;;
  stop)
    systemctl stop nubezero
    echo -e "${GREEN}Servicio detenido.${NC}"
    ;;
  restart)
    systemctl restart nubezero
    echo -e "${GREEN}Servicio reiniciado.${NC}"
    ;;
  logs)
    journalctl -u nubezero -f
    ;;
  *)
    echo "Uso: nubezero [opción]"
    echo ""
    echo "Opciones:"
    echo "  -c, config   Abre el asistente de configuración (Almacenamiento, Optimización, etc.)"
    echo "  -u, update   Actualiza el servidor a la última versión"
    echo "  start        Inicia el servicio"
    echo "  stop         Detiene el servicio"
    echo "  restart      Reinicia el servicio"
    echo "  logs         Muestra los logs del servidor en tiempo real"
    ;;
esac
