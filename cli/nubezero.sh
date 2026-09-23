#!/bin/bash
# Nube-Zero CLI - Herramienta Modular de Configuración y Gestión

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

function pause_menu() {
  echo -e "\n${CYAN}Presiona Enter para continuar...${NC}"
  read -r
}

# Helper para actualizar ExecStart en el archivo systemd sin duplicar lógica ni romper flags
function update_systemd_service() {
  local target_port=$1
  local target_name=$2
  local target_storage=$3

  if [ ! -f "$SERVICE_FILE" ]; then
    echo -e "${RED}Archivo de servicio $SERVICE_FILE no encontrado.${NC}"
    return 1
  fi

  local current_port=$(grep "ExecStart" "$SERVICE_FILE" 2>/dev/null | grep -oP '(?<=--port )\d+')
  if [ -z "$current_port" ]; then current_port=8080; fi
  if [ -n "$target_port" ]; then current_port=$target_port; fi

  local current_name=$(grep "ExecStart" "$SERVICE_FILE" 2>/dev/null | grep -oP '(?<=--name ")[^"]+' || grep "ExecStart" "$SERVICE_FILE" 2>/dev/null | grep -oP '(?<=--name )\S+')
  if [ "$target_name" == "__CLEAR__" ]; then
    current_name=""
  elif [ -n "$target_name" ]; then
    current_name="$target_name"
  fi

  local current_storage=$(grep "ExecStart" "$SERVICE_FILE" 2>/dev/null | grep -oP '(?<=--storage )\S+')
  if [ "$target_storage" == "__CLEAR__" ]; then
    current_storage=""
  elif [ -n "$target_storage" ]; then
    current_storage="$target_storage"
  fi

  local new_cmd="ExecStart=/usr/bin/mono $INSTALL_DIR/bin/NubeZero.Server.exe --port $current_port"
  if [ -n "$current_storage" ]; then
    new_cmd="$new_cmd --storage $current_storage"
  fi
  if [ -n "$current_name" ]; then
    new_cmd="$new_cmd --name \"$current_name\""
  fi

  sed -i "s|ExecStart=.*|$new_cmd|g" "$SERVICE_FILE"
  systemctl daemon-reload
}

function show_status_dashboard() {
  local svc_status="${RED}Detenido (inactive)${NC}"
  if systemctl is-active --quiet nubezero 2>/dev/null; then
    svc_status="${GREEN}● Activo (running)${NC}"
  fi

  local current_port=$(grep "ExecStart" "$SERVICE_FILE" 2>/dev/null | grep -oP '(?<=--port )\d+')
  if [ -z "$current_port" ]; then current_port=8080; fi

  local current_name=$(grep "ExecStart" "$SERVICE_FILE" 2>/dev/null | grep -oP '(?<=--name ")[^"]+' || grep "ExecStart" "$SERVICE_FILE" 2>/dev/null | grep -oP '(?<=--name )\S+')
  if [ -z "$current_name" ]; then current_name="(Por defecto: $(hostname))"; fi

  local current_storage=$(grep "ExecStart" "$SERVICE_FILE" 2>/dev/null | grep -oP '(?<=--storage )\S+')
  local storage_info="MicroSD local ($INSTALL_DIR/Storage)"
  if [ -n "$current_storage" ]; then
    if mountpoint -q "$current_storage" 2>/dev/null; then
      local disk_space=$(df -h "$current_storage" 2>/dev/null | awk 'NR==2 {print $4 " libres de " $2}')
      storage_info="USB en $current_storage ($disk_space)"
    else
      storage_info="USB en $current_storage (${YELLOW}No montada${NC})"
    fi
  fi

  local sd_status="${YELLOW}🔓 Lectura/Escritura (rw)${NC}"
  if awk '$2 == "/" { print $4 }' /proc/mounts 2>/dev/null | grep -q '\bro\b'; then
    sd_status="${GREEN}🛡️ Solo Lectura (ro) - Protegida${NC}"
  elif grep -q ",ro" /etc/fstab 2>/dev/null; then
    sd_status="${CYAN}🛡️ Blindada en fstab (activo al reiniciar)${NC}"
  fi

  echo -e "\n${CYAN}┌──────────────── Estado Actual del Servidor ────────────────┐${NC}"
  echo -e "${CYAN}│${NC}  • Servicio:        $svc_status"
  echo -e "${CYAN}│${NC}  • Nombre en Red:   ${YELLOW}$current_name${NC}"
  echo -e "${CYAN}│${NC}  • Puerto HTTP:     ${YELLOW}$current_port${NC}"
  echo -e "${CYAN}│${NC}  • Almacenamiento:  ${YELLOW}$storage_info${NC}"
  echo -e "${CYAN}│${NC}  • Tarjeta MicroSD: $sd_status"
  echo -e "${CYAN}└────────────────────────────────────────────────────────────┘${NC}"
}

function menu_change_name() {
  print_header
  local current_name=$(grep "ExecStart" "$SERVICE_FILE" 2>/dev/null | grep -oP '(?<=--name ")[^"]+' || grep "ExecStart" "$SERVICE_FILE" 2>/dev/null | grep -oP '(?<=--name )\S+')
  echo -e "${CYAN}--- Cambiar Nombre en Red del Servidor ---${NC}"
  echo -e "Nombre actual: ${YELLOW}${current_name:-"(Por defecto: $(hostname))"}${NC}"
  echo -ne "Introduce el nuevo nombre (deja vacío para cancelar): "
  read NEW_NAME
  if [ -n "$NEW_NAME" ]; then
    update_systemd_service "" "$NEW_NAME" ""
    systemctl restart nubezero
    echo -e "${GREEN}✓ Nombre actualizado a '$NEW_NAME' y servicio reiniciado.${NC}"
  else
    echo -e "${YELLOW}Operación cancelada. No se realizaron cambios.${NC}"
  fi
  pause_menu
}

function menu_change_port() {
  print_header
  local current_port=$(grep "ExecStart" "$SERVICE_FILE" 2>/dev/null | grep -oP '(?<=--port )\d+')
  if [ -z "$current_port" ]; then current_port=8080; fi
  echo -e "${CYAN}--- Cambiar Puerto HTTP ---${NC}"
  echo -e "Puerto actual: ${YELLOW}$current_port${NC}"
  echo -ne "Introduce el nuevo puerto [1-65535] (deja vacío para cancelar): "
  read NEW_PORT
  if [ -n "$NEW_PORT" ] && [[ "$NEW_PORT" =~ ^[0-9]+$ ]]; then
    update_systemd_service "$NEW_PORT" "" ""
    systemctl restart nubezero
    echo -e "${GREEN}✓ Puerto actualizado a $NEW_PORT y servicio reiniciado.${NC}"
  else
    echo -e "${YELLOW}Operación cancelada o puerto no válido.${NC}"
  fi
  pause_menu
}

function menu_storage() {
  print_header
  echo -e "${CYAN}--- Gestión de Almacenamiento ---${NC}"
  local current_storage=$(grep "ExecStart" "$SERVICE_FILE" 2>/dev/null | grep -oP '(?<=--storage )\S+')
  if [ -n "$current_storage" ]; then
    echo -e "Almacenamiento actual: ${GREEN}$current_storage${NC}"
  else
    echo -e "Almacenamiento actual: ${YELLOW}MicroSD local ($INSTALL_DIR/Storage)${NC}"
  fi

  echo ""
  echo "  1) Conectar / Montar memoria USB (OTG)"
  echo "  2) Desvincular almacenamiento USB y volver a MicroSD local"
  echo "  0) Volver al menú principal"
  echo ""
  echo -ne "${CYAN}Selecciona una opción [0-2]: ${NC}"
  read ST_OPT

  case "$ST_OPT" in
    1)
      echo -e "\nDispositivos de almacenamiento detectados:"
      lsblk -o NAME,SIZE,TYPE,MOUNTPOINT | grep -v 'loop' | grep -v 'rom'
      
      echo -e "\nIntroduce el nombre de la partición a usar (Ej: sda1 o sdb1, deja vacío para cancelar):"
      read DISK_NAME
      if [ -z "$DISK_NAME" ]; then
        echo -e "${YELLOW}Operación cancelada.${NC}"
        pause_menu
        return
      fi

      if [ ! -b "/dev/$DISK_NAME" ]; then
        echo -e "${RED}Error: Dispositivo /dev/$DISK_NAME no encontrado.${NC}"
        pause_menu
        return
      fi

      echo -e "\n${YELLOW}¿Deseas FORMATEAR /dev/$DISK_NAME a ext4?${NC}"
      echo -e "${RED}⚠️  Si respondes 'S', se borrará TODO el contenido del dispositivo.${NC}"
      echo -e "Si la memoria ya contiene tus archivos de Nube-Zero, responde 'N'."
      echo -ne "¿Formatear partición? [s/N]: "
      read FORMAT_DISK

      if [[ "$FORMAT_DISK" == "s" || "$FORMAT_DISK" == "S" ]]; then
        echo "Formateando /dev/$DISK_NAME a ext4..."
        mkfs.ext4 -F "/dev/$DISK_NAME"
      fi

      MOUNT_DIR="/mnt/nubezero_usb"
      mkdir -p "$MOUNT_DIR"

      # Asegurar montaje en /etc/fstab
      if ! grep -q "$MOUNT_DIR" /etc/fstab; then
        echo "/dev/$DISK_NAME $MOUNT_DIR ext4 defaults,noatime 0 2" >> /etc/fstab
      else
        sed -i "s|.*$MOUNT_DIR.*|/dev/$DISK_NAME $MOUNT_DIR ext4 defaults,noatime 0 2|g" /etc/fstab
      fi

      mount -a
      chown -R root:root "$MOUNT_DIR"

      update_systemd_service "" "" "$MOUNT_DIR"
      systemctl restart nubezero
      echo -e "${GREEN}✓ Almacenamiento configurado en $MOUNT_DIR y servicio reiniciado.${NC}"
      pause_menu
      ;;

    2)
      echo -e "\n${YELLOW}¿Estás seguro de desvincular el USB y volver a almacenar en la MicroSD? [s/N]${NC}"
      read CONFIRM_UNLINK
      if [[ "$CONFIRM_UNLINK" == "s" || "$CONFIRM_UNLINK" == "S" ]]; then
        update_systemd_service "" "" "__CLEAR__"
        systemctl restart nubezero
        echo -e "${GREEN}✓ Se ha vuelto al almacenamiento local de la MicroSD.${NC}"
      else
        echo -e "${YELLOW}Operación cancelada.${NC}"
      fi
      pause_menu
      ;;

    0|*)
      return
      ;;
  esac
}

function menu_blindaje() {
  print_header
  echo -e "${CYAN}--- Blindaje de Almacenamiento MicroSD (Solo Lectura) ---${NC}"
  echo -e "Protege la MicroSD contra corrupción por apagones y elimina escrituras del sistema."
  echo ""
  
  local is_ro=false
  if awk '$2 == "/" { print $4 }' /proc/mounts 2>/dev/null | grep -q '\bro\b' || grep -q ",ro" /etc/fstab 2>/dev/null; then
    is_ro=true
    echo -e "Estado del blindaje: ${GREEN}ACTIVADO (Solo Lectura)${NC}"
  else
    echo -e "Estado del blindaje: ${YELLOW}DESACTIVADO (Lectura/Escritura)${NC}"
  fi

  echo ""
  echo "  1) Activar Blindaje MicroSD (Redirige logs/tmp a USB y congela MicroSD en 'ro')"
  echo "  2) Desactivar Blindaje MicroSD (Volver a Lectura/Escritura permanente)"
  echo "  0) Volver al menú principal"
  echo ""
  echo -ne "${CYAN}Selecciona una opción [0-2]: ${NC}"
  read RO_OPT

  case "$RO_OPT" in
    1)
      if [ ! -d "/mnt/nubezero_usb" ]; then
        echo -e "${RED}Error: Debes tener una memoria USB montada en /mnt/nubezero_usb antes de activar el blindaje.${NC}"
        pause_menu
        return
      fi

      echo -e "Configurando Bind Mounts hacia la memoria USB..."
      mkdir -p /mnt/nubezero_usb/basurero/log /mnt/nubezero_usb/basurero/tmp
      rsync -a /var/log/ /mnt/nubezero_usb/basurero/log/ 2>/dev/null || true

      if ! grep -q "/var/log" /etc/fstab; then
        echo "/mnt/nubezero_usb/basurero/log  /var/log  none  bind  0  0" >> /etc/fstab
        echo "/mnt/nubezero_usb/basurero/tmp  /tmp      none  bind  0  0" >> /etc/fstab
        echo "/mnt/nubezero_usb/basurero/tmp  /var/tmp  none  bind  0  0" >> /etc/fstab
      fi

      # Resolver DNS dinámico
      rm -f /etc/resolv.conf
      ln -s /tmp/resolv.conf /etc/resolv.conf
      echo -e "nameserver 1.1.1.1\nnameserver 8.8.8.8" > /tmp/resolv.conf 2>/dev/null || true

      # Añadir ,ro a la raíz en fstab
      awk '$2 == "/" { if ($4 !~ /ro/) $4 = $4 ",ro" } 1' /etc/fstab > /etc/fstab.tmp && mv /etc/fstab.tmp /etc/fstab
      awk '$2 == "/boot/firmware" { if ($4 !~ /ro/) $4 = $4 ",ro" } 1' /etc/fstab > /etc/fstab.tmp && mv /etc/fstab.tmp /etc/fstab

      # Alias de consola
      if ! grep -q "alias ro=" /etc/bash.bashrc; then
        echo "alias rw='sudo mount -o remount,rw / && sudo mount -o remount,rw /boot/firmware && echo \"SD Desbloqueada (Modo Escritura)\"'" >> /etc/bash.bashrc
        echo "alias ro='sudo mount -o remount,ro / && sudo mount -o remount,ro /boot/firmware && echo \"SD Congelada (Solo Lectura)\"'" >> /etc/bash.bashrc
      fi

      echo -e "${GREEN}✓ Blindaje configurado con éxito.${NC}"
      echo -e "${YELLOW}La MicroSD quedará estrictamente congelada en Solo Lectura al reiniciar ('sudo reboot').${NC}"
      echo -e "${CYAN}Recuerda que dispones de los comandos 'rw' y 'ro' para mantenimientos futuros.${NC}"
      pause_menu
      ;;

    2)
      echo "Desactivando modo Solo Lectura en /etc/fstab..."
      sed -i 's/,ro//g' /etc/fstab
      sed -i 's/ro,//g' /etc/fstab
      mount -o remount,rw / 2>/dev/null || true
      mount -o remount,rw /boot/firmware 2>/dev/null || true
      echo -e "${GREEN}✓ Blindaje desactivado. El sistema ahora permanecerá en Lectura/Escritura.${NC}"
      pause_menu
      ;;

    0|*)
      return
      ;;
  esac
}

function menu_optimization() {
  print_header
  echo -e "${CYAN}--- Optimización del Sistema (Headless Extrema) ---${NC}"
  echo -e "Deshabilita Swap, activa ZRAM comprimido, reduce RAM de video a 16MB y apaga servicios no esenciales."
  echo ""
  echo -ne "¿Deseas aplicar las optimizaciones extremas para Raspberry Pi? [s/N]: "
  read CONFIRM_OPT

  if [[ "$CONFIRM_OPT" == "s" || "$CONFIRM_OPT" == "S" ]]; then
    echo "Aplicando optimizaciones..."

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

    # 3. Deshabilitar servicios innecesarios
    systemctl disable --now apt-daily.timer apt-daily-upgrade.timer man-db.timer bluetooth hciuart triggerhappy avahi-daemon 2>/dev/null || true

    # 4. Reducir memoria de GPU a 16MB
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

    echo -e "${GREEN}✓ Optimizaciones extremas aplicadas con éxito.${NC}"
  else
    echo -e "${YELLOW}Operación cancelada.${NC}"
  fi
  pause_menu
}

function change_admin_password() {
  print_header
  echo -e "${CYAN}--- Restablecer Contraseña de Administrador ---${NC}"
  echo -ne "Introduce la nueva contraseña para el administrador: "
  read -s NEW_PASS
  echo ""
  echo -ne "Confirma la nueva contraseña: "
  read -s CONFIRM_PASS
  echo ""

  if [ -z "$NEW_PASS" ]; then
    echo -e "${RED}La contraseña no puede estar vacía.${NC}"
    pause_menu
    return
  fi

  if [ "$NEW_PASS" != "$CONFIRM_PASS" ]; then
    echo -e "${RED}Las contraseñas no coinciden. No se realizaron cambios.${NC}"
    pause_menu
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
" | grep -q "SUCCESS" && echo -e "${GREEN}✓ ¡Contraseña de administrador actualizada con éxito!${NC}" || echo -e "${RED}No se pudo actualizar la contraseña en $DB_FILE.${NC}"
  else
    echo -e "${YELLOW}Base de datos no encontrada. La contraseña se configurará al iniciar Nube-Zero.${NC}"
  fi
  pause_menu
}

function run_config() {
  while true; do
    clear 2>/dev/null || true
    print_header
    show_status_dashboard

    echo -e "${YELLOW}Menú de Configuración Modular:${NC}"
    echo "  1) 🏷️  Cambiar Nombre del Servidor en Red"
    echo "  2) 🌐 Cambiar Puerto HTTP"
    echo "  3) 💾 Gestionar Almacenamiento (USB / MicroSD)"
    echo "  4) 🛡️  Gestionar Blindaje MicroSD (Solo Lectura)"
    echo "  5) ⚡ Optimización del Sistema (Headless Extrema)"
    echo "  6) 🔑 Restablecer Contraseña de Administrador"
    echo "  7) 🔄 Reiniciar Servicio Nube-Zero"
    echo "  8) 📜 Ver Logs del Servicio en Tiempo Real"
    echo "  9) 🚀 Actualizar SOLO esta herramienta CLI (desde main)"
    echo "  0) 🚪 Salir"
    echo ""
    echo -ne "${CYAN}Selecciona una opción [0-9]: ${NC}"
    read -r OPTION

    case "$OPTION" in
      1) menu_change_name ;;
      2) menu_change_port ;;
      3) menu_storage ;;
      4) menu_blindaje ;;
      5) menu_optimization ;;
      6) change_admin_password ;;
      7)
        systemctl restart nubezero
        echo -e "${GREEN}✓ Servicio reiniciado.${NC}"
        pause_menu
        ;;
      8)
        echo -e "${YELLOW}(Presiona Ctrl+C para volver al menú)${NC}"
        journalctl -u nubezero -n 50 -f
        ;;
      9)
        update_cli
        ;;
      0|q|Q)
        echo -e "\n${GREEN}Saliendo de la configuración de Nube-Zero.${NC}"
        break
        ;;
      *)
        echo -e "${RED}Opción no válida.${NC}"
        sleep 1
        ;;
    esac
  done
}

function update_cli() {
  print_header
  echo -e "${YELLOW}Actualizando exclusivamente la herramienta CLI (nubezero)...${NC}"
  echo -e "Descargando la versión más reciente directamente desde GitHub (main)..."

  mount -o remount,rw / 2>/dev/null || true
  mount -o remount,rw /boot/firmware 2>/dev/null || true

  local tmp_cli
  tmp_cli=$(mktemp)
  local cli_url="https://raw.githubusercontent.com/RichyKunBv/Nube-Zero/main/cli/nubezero.sh"

  if wget -q "$cli_url" -O "$tmp_cli" 2>/dev/null || curl -sL "$cli_url" -o "$tmp_cli" 2>/dev/null; then
    if bash -n "$tmp_cli"; then
      cp "$tmp_cli" /usr/local/bin/nubezero
      chmod +x /usr/local/bin/nubezero
      rm -f "$tmp_cli"
      echo -e "${GREEN}✓ CLI de Nube-Zero actualizada con éxito a la última versión.${NC}"
      echo -e "${CYAN}Ya estás ejecutando los cambios más recientes sin necesidad de actualizar todo el servidor.${NC}"
    else
      echo -e "${RED}Error: El archivo descargado contiene errores de sintaxis. No se aplicaron cambios.${NC}"
      rm -f "$tmp_cli"
    fi
  else
    echo -e "${RED}Error al descargar la última versión del CLI desde GitHub.${NC}"
    rm -f "$tmp_cli"
  fi
  pause_menu
}

function run_update() {
  print_header
  echo -e "${YELLOW}Actualizando servidor Nube-Zero...${NC}"
  
  TMP_SCRIPT=$(mktemp)
  curl -sL https://raw.githubusercontent.com/RichyKunBv/Nube-Zero/main/setup_server.sh -o "$TMP_SCRIPT"
  chmod +x "$TMP_SCRIPT"
  
  "$TMP_SCRIPT" update
  rm -f "$TMP_SCRIPT"
}

case "$1" in
  -c|config)
    run_config
    ;;
  update-cli|-u-cli|cli-update)
    update_cli
    ;;
  -u|update)
    echo -e "${CYAN}¿Qué deseas actualizar?${NC}"
    echo "  1) Servidor Nube-Zero completo (Última Release oficial)"
    echo "  2) Solo esta herramienta CLI (nubezero desde main con wget)"
    echo -ne "Opción [1-2]: "
    read -r UP_CHOICE
    if [ "$UP_CHOICE" == "2" ]; then
      update_cli
    else
      run_update
    fi
    ;;
  status)
    print_header
    show_status_dashboard
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
    echo "  -c, config       Abre el panel interactivo modular de configuración"
    echo "  status           Muestra el estado actual del servidor (dashboard)"
    echo "  -u, update       Menú de actualización (Servidor o CLI)"
    echo "  update-cli       Actualiza exclusivamente la herramienta CLI desde main"
    echo "  passwd           Restablece la contraseña del usuario administrador"
    echo "  start            Inicia el servicio"
    echo "  stop             Detiene el servicio"
    echo "  restart          Reinicia el servicio"
    echo "  logs             Muestra los logs del servidor en tiempo real"
    ;;
esac
