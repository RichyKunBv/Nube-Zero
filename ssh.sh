#!/bin/bash

GREEN='\033[1;32m'
NC='\033[0m'
CYAN='\033[0;36m'
RED='\033[0;31m'

usuario (){
    read -p "  >> Introduce el usuario: " USER
}

hostname (){
    read -p "  >> Introduce el hostname: " HOST
}


echo -e "${GREEN}======================================================${NC}"
echo -e "${CYAN}Por favor, introduce los datos de conexión:${NC}"

usuario
hostname

while true; do
    echo -e "${GREEN}======================================================${NC}"
    echo -e "${GREEN}Conectándote como ${USER}@${HOST}... ¡Suerte, rey!${NC}"
    
    ssh $USER@$HOST

    if [ $? -eq 0 ]; then
        echo -e "${GREEN}Sesión cerrada correctamente. ¡Nos vemos!${NC}"
        break 
    else
        echo -e "\n${RED}¡Uff, la conexión falló! ¿Qué hacemos?${NC}"
        echo "  1. Cambiar usuario (actual: $USER)"
        echo "  2. Cambiar hostname (actual: $HOST)"
        echo "  3. Reintentar con los mismos datos"
        echo "  4. Salir"
        read -p "  >> Elige una opción: " choice

        case $choice in
            1)
                usuario 
                ;;
            2)
                hostname 
                ;;
            3)
                continue 
                ;;
            4)
                echo "Ok, saliendo."
                exit 1
                ;;
            *)
                echo -e "${RED}Esa opción no existe, elige otra.${NC}"
                read -p "Presiona Enter para continuar..."
                ;;
        esac
    fi
done