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

# Autogestión de permisos (Si el sistema está en Solo Lectura)
mount -o remount,rw / 2>/dev/null || true
mount -o remount,rw /boot/firmware 2>/dev/null || true

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
          
          # Actualizar systemd manteniendo el puerto y nombre actual
          CURRENT_PORT=$(grep "ExecStart" "$SERVICE_FILE" | grep -oP '(?<=--port )\d+')
          if [ -z "$CURRENT_PORT" ]; then CURRENT_PORT=8080; fi
          CURRENT_NAME=$(grep "ExecStart" "$SERVICE_FILE" | grep -oP '(?<=--name ")[^"]+' || grep "ExecStart" "$SERVICE_FILE" | grep -oP '(?<=--name )\S+')
          EXTRA_NAME=""
          if [ -n "$CURRENT_NAME" ]; then EXTRA_NAME=" --name \"$CURRENT_NAME\""; fi
          
          sed -i "s|ExecStart=.*|ExecStart=/usr/bin/mono $INSTALL_DIR/bin/NubeZero.Server.exe --port $CURRENT_PORT --storage $MOUNT_DIR$EXTRA_NAME|g" "$SERVICE_FILE"
          echo -e "${GREEN}Almacenamiento configurado en $MOUNT_DIR${NC}"
          
          echo -e "\n${YELLOW}¿Deseas activar el Blindaje de Almacenamiento (Modo Solo Lectura en la MicroSD para evitar desgaste)? [y/N]${NC}"
          read ENABLE_RO
          if [[ "$ENABLE_RO" == "y" || "$ENABLE_RO" == "Y" ]]; then
              echo -e "Configurando Bind Mounts y Modo Solo Lectura..."
              mkdir -p /mnt/nubezero_usb/basurero/log /mnt/nubezero_usb/basurero/tmp
              rsync -a /var/log/ /mnt/nubezero_usb/basurero/log/ 2>/dev/null || true
              
              if ! grep -q "/var/log" /etc/fstab; then
                  echo "/mnt/nubezero_usb/basurero/log  /var/log  none  bind  0  0" >> /etc/fstab
                  echo "/mnt/nubezero_usb/basurero/tmp  /tmp      none  bind  0  0" >> /etc/fstab
                  echo "/mnt/nubezero_usb/basurero/tmp  /var/tmp  none  bind  0  0" >> /etc/fstab
              fi
              
              # Configurar resolución de nombres DNS dinámica
              rm -f /etc/resolv.conf
              ln -s /tmp/resolv.conf /etc/resolv.conf
              echo -e "nameserver 1.1.1.1\nnameserver 8.8.8.8" > /tmp/resolv.conf 2>/dev/null || true
              
              awk '$2 == "/" { if ($4 !~ /ro/) $4 = $4 ",ro" } 1' /etc/fstab > /etc/fstab.tmp && mv /etc/fstab.tmp /etc/fstab
              awk '$2 == "/boot/firmware" { if ($4 !~ /ro/) $4 = $4 ",ro" } 1' /etc/fstab > /etc/fstab.tmp && mv /etc/fstab.tmp /etc/fstab
              
              if ! grep -q "alias ro=" /etc/bash.bashrc; then
                  echo "alias rw='sudo mount -o remount,rw / && sudo mount -o remount,rw /boot/firmware && echo \"SD Desbloqueada (Modo Escritura)\"'" >> /etc/bash.bashrc
                  echo "alias ro='sudo mount -o remount,ro / && sudo mount -o remount,ro /boot/firmware && echo \"SD Congelada (Solo Lectura)\"'" >> /etc/bash.bashrc
              fi
              echo -e "${GREEN}Blindaje activado. La MicroSD quedará en Solo Lectura al reiniciar.${NC}"
              echo -e "${CYAN}Nota: En el futuro puedes usar el comando 'rw' para modificar el sistema, y 'ro' para bloquearlo de nuevo.${NC}"
          fi
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
              rm -f "$INSTALL_DIR/database.json" "$INSTALL_DIR/bin/database.json"
          fi
          
          CURRENT_PORT=$(grep "ExecStart" "$SERVICE_FILE" | grep -oP '(?<=--port )\d+')
          if [ -z "$CURRENT_PORT" ]; then CURRENT_PORT=8080; fi
          CURRENT_NAME=$(grep "ExecStart" "$SERVICE_FILE" | grep -oP '(?<=--name ")[^"]+' || grep "ExecStart" "$SERVICE_FILE" | grep -oP '(?<=--name )\S+')
          EXTRA_NAME=""
          if [ -n "$CURRENT_NAME" ]; then EXTRA_NAME=" --name \"$CURRENT_NAME\""; fi
          
          sed -i "s|ExecStart=.*|ExecStart=/usr/bin/mono $INSTALL_DIR/bin/NubeZero.Server.exe --port $CURRENT_PORT$EXTRA_NAME|g" "$SERVICE_FILE"
          echo -e "${GREEN}Se ha vuelto al almacenamiento de la MicroSD.${NC}"
      fi
  fi
  
  # 2. Rendimiento
  echo -e "\n${CYAN}2. Optimización del Sistema${NC}"
  echo -e "¿Deseas aplicar la Optimización Headless Extrema (Recomendado para uso exclusivo de Nube-Zero)? [y/N]"
  echo -e "Esto deshabilitará Swap, activará ZRAM, reducirá RAM de video y apagará servicios innecesarios."
  read EXCLUSIVE_USE
  if [[ "$EXCLUSIVE_USE" == "y" || "$EXCLUSIVE_USE" == "Y" ]]; then
      echo -e "Aplicando optimizaciones extremas (Tardará unos segundos)..."
      
      # 1. Purgar dphys-swapfile
      if systemctl is-active --quiet dphys-swapfile; then
          dphys-swapfile swapoff || true
          systemctl disable dphys-swapfile || true
          apt-get purge -y dphys-swapfile || true
          rm -f /var/swap || true
      fi
      
      # 2. Instalar ZRAM
      apt-get update -y > /dev/null
      apt-get install -y zram-tools > /dev/null
      echo -e "ALGO=lz4\nPERCENT=50" > /etc/default/zramswap
      systemctl restart zramswap || true
      echo "vm.swappiness=100" > /etc/sysctl.d/99-zram.conf
      sysctl -p /etc/sysctl.d/99-zram.conf > /dev/null 2>&1 || true
      
      # 3. Deshabilitar tareas pesadas y servicios inútiles
      systemctl disable --now apt-daily.timer apt-daily-upgrade.timer man-db.timer bluetooth hciuart triggerhappy avahi-daemon 2>/dev/null || true
      
      # 4. Reducir RAM de GPU
      if [ -f /boot/firmware/config.txt ]; then
          if ! grep -q "^gpu_mem=" /boot/firmware/config.txt; then
              echo "gpu_mem=16" >> /boot/firmware/config.txt
          else
              sed -i 's/^gpu_mem=.*/gpu_mem=16/' /boot/firmware/config.txt
          fi
      fi
      
      # 5. Deshabilitar IPv6
      echo -e "net.ipv6.conf.all.disable_ipv6=1\nnet.ipv6.conf.default.disable_ipv6=1" > /etc/sysctl.d/99-disable-ipv6.conf
      sysctl -p /etc/sysctl.d/99-disable-ipv6.conf > /dev/null 2>&1 || true
      
      echo -e "${GREEN}Optimizaciones extremas aplicadas con éxito.${NC}"
  fi
  
  # 3. Contraseña de Administrador
  echo -e "\n${CYAN}3. Contraseña de Administrador${NC}"
  echo -e "¿Deseas cambiar o restablecer la contraseña del usuario administrador? [y/N]"
  read CHANGE_ADMIN
  if [[ "$CHANGE_ADMIN" == "y" || "$CHANGE_ADMIN" == "Y" ]]; then
      change_admin_password
  fi
  
  systemctl daemon-reload
  systemctl restart nubezero
  echo -e "\n${GREEN}¡Configuración completada y servicio reiniciado!${NC}"
}

function change_admin_password() {
  print_header
  echo -e "${CYAN}--- Restablecer Contraseña de Administrador ---${NC}"
  echo -e "Introduce la nueva contraseña para el administrador:"
  read -s NEW_PASS
  echo ""
  echo -e "Confirma la nueva contraseña:"
  read -s CONFIRM_PASS
  echo ""
  
  if [ -z "$NEW_PASS" ]; then
      echo -e "${RED}La contraseña no puede estar vacía.${NC}"
      return
  fi
  
  if [ "$NEW_PASS" != "$CONFIRM_PASS" ]; then
      echo -e "${RED}Las contraseñas no coinciden. No se realizaron cambios.${NC}"
      return
  fi

  PASS_HASH=$(echo -n "$NEW_PASS" | sha256sum | awk '{print $1}')
  
  DB_FILE=""
  if [ -f "/mnt/nubezero_usb/database.json" ]; then
      DB_FILE="/mnt/nubezero_usb/database.json"
  elif [ -f "$INSTALL_DIR/bin/database.json" ]; then
      DB_FILE="$INSTALL_DIR/bin/database.json"
  elif [ -f "$INSTALL_DIR/database.json" ]; then
      DB_FILE="$INSTALL_DIR/database.json"
  fi

  if [ -n "$DB_FILE" ] && [ -f "$DB_FILE" ]; then
      python3 -c "
import json
try:
    with open('$DB_FILE', 'r') as f:
        data = json.load(f)
    found = False
    for u in data.get('Usuarios', []):
        if u.get('Role') == 'Admin' or u.get('Username', '').lower() == 'admin':
            u['PasswordHash'] = '$PASS_HASH'
            u['Role'] = 'Admin'
            found = True
            break
    if not found and len(data.get('Usuarios', [])) > 0:
        data['Usuarios'][0]['PasswordHash'] = '$PASS_HASH'
        data['Usuarios'][0]['Role'] = 'Admin'
        found = True
    if found:
        with open('$DB_FILE', 'w') as f:
            json.dump(data, f, indent=2)
        print('SUCCESS')
    else:
        print('NO_USER')
except Exception as e:
    print('ERROR:', e)
" | grep -q "SUCCESS" && echo -e "${GREEN}¡Contraseña de administrador actualizada con éxito!${NC}" || echo -e "${RED}No se pudo actualizar la contraseña en $DB_FILE.${NC}"
  else
      echo -e "${YELLOW}Base de datos no encontrada. La contraseña se configurará al iniciar Nube-Zero.${NC}"
  fi
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
  passwd|password)
    change_admin_password
    systemctl restart nubezero
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
    echo "  -c, config       Abre el asistente de configuración (Almacenamiento, Optimización, Contraseña)"
    echo "  -u, update       Actualiza el servidor a la última versión"
    echo "  passwd           Restablece la contraseña del usuario administrador"
    echo "  start            Inicia el servicio"
    echo "  stop             Detiene el servicio"
    echo "  restart          Reinicia el servicio"
    echo "  logs             Muestra los logs del servidor en tiempo real"
    ;;
esac
