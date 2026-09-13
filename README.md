<div align="center">

# Nube-Zero ☁️

[![Versión](https://img.shields.io/badge/Versión-v0.4.2-blue.svg)](https://github.com/RichyKunBv/Nube-Zero)
[![Status](https://img.shields.io/badge/Estado-Stable-green.svg)](https://github.com/RichyKunBv/Nube-Zero)
[![Licencia](https://img.shields.io/badge/Licencia-Apache_2.0-orange.svg)](https://github.com/RichyKunBv/Nube-Zero/blob/main/LICENSE)
[![Lenguaje](https://img.shields.io/badge/Lenguaje-C%23_.NET_10.0-lightgrey.svg)](https://dotnet.microsoft.com/es-es/download/dotnet/10.0)
[![GUI](https://img.shields.io/badge/GUI-Avalonia_UI-purple.svg)](https://avaloniaui.net)

Tu solución personal de nube ligera, rápida y multiplataforma.

</div>

---

## 🚀 Instalación y Descargas

### Descargar para Cliente (Desktop & Mobile)

Nube-Zero cuenta con un cliente nativo súper rápido construido en Avalonia UI. Descarga la versión adecuada para tu sistema:

| ![Windows](https://img.shields.io/badge/Windows-000000?style=for-the-badge&logo=windows&logoColor=white) | ![macOS](https://img.shields.io/badge/macOS-000000?style=for-the-badge&logo=apple&logoColor=white) | ![Linux](https://img.shields.io/badge/Linux-000000?style=for-the-badge&logo=linux&logoColor=white) | ![Android](https://img.shields.io/badge/Android-000000?style=for-the-badge&logo=android&logoColor=white) |
| :---: | :---: | :---: | :---: |
| [![Windows ARM](https://img.shields.io/badge/Windows%20ARM-000000?style=for-the-badge&logo=windows&logoColor=white)](https://github.com/RichyKunBv/Nube-Zero/releases/latest/download/NubeZero-arm64.exe) | [![macOS ARM](https://img.shields.io/badge/macOS%20ARM-000000?style=for-the-badge&logoColor=white)](https://github.com/RichyKunBv/Nube-Zero/releases/latest/download/NubeZero-arm64.dmg) | [![Linux ARM](https://img.shields.io/badge/Linux%20ARM-000000?style=for-the-badge&logoColor=white)](https://github.com/RichyKunBv/Nube-Zero/releases/latest/download/NubeZero-arm64.AppImage) | [![Android APK](https://img.shields.io/badge/Android%20APK-000000?style=for-the-badge&logo=android&logoColor=white)](https://github.com/RichyKunBv/Nube-Zero/releases/latest/download/NubeZero.apk) |
| [![Windows X64](https://img.shields.io/badge/Windows%20X64-000000?style=for-the-badge&logo=windows&logoColor=white)](https://github.com/RichyKunBv/Nube-Zero/releases/latest/download/NubeZero-x64.exe) | [![macOS X64](https://img.shields.io/badge/macOS%20X64-000000?style=for-the-badge&logoColor=white)](https://github.com/RichyKunBv/Nube-Zero/releases/latest/download/NubeZero-x64.dmg) | [![Linux X64](https://img.shields.io/badge/Linux%20X64-000000?style=for-the-badge&logoColor=white)](https://github.com/RichyKunBv/Nube-Zero/releases/latest/download/NubeZero-x64.AppImage) | |

### Servidor (Raspberry Pi / Linux)

> [!IMPORTANT]
> Para instalar, configurar o actualizar el servidor en tu Raspberry Pi o entorno Linux, **DEBES utilizar el script `setup_server.sh`**. 

El script automatizará la descarga de binarios, configuración de dependencias (Mono) y establecimiento de servicios systemd para que arranque automáticamente.

Puedes descargarlo y ejecutarlo usando:

```bash
wget https://raw.githubusercontent.com/RichyKunBv/Nube-Zero/main/setup_server.sh
sudo bash setup_server.sh
```

*(Recuerda ejecutarlo con permisos de administrador o `sudo`)*

---

## 🛡️ Blindaje y Optimización para Raspberry Pi

Nube-Zero incluye una herramienta de configuración de línea de comandos para transformar tu Raspberry Pi en un appliance (electrodoméstico) ultrarrápido y seguro a nivel hardware.

Ejecutando el asistente interactivo:
```bash
sudo nubezero config
```
Podrás activar opciones avanzadas:
1. **Blindaje de Almacenamiento (Desgaste Cero):** Trasladará de forma nativa los registros (`/var/log`, `/tmp`) hacia la memoria USB e instaurará el modo **Solo Lectura (`ro`)** en la MicroSD. Esto garantiza que la tarjeta SD dure años sin desgastarse. En el futuro, si deseas modificar tu sistema, simplemente escribe `rw` en tu terminal para habilitar escritura, y `ro` para volverla a bloquear.
2. **Optimización Headless Extrema:** Deshabilitará el paginado físico de disco (Swap), activará **ZRAM** (compresión en RAM) para maximizar la memoria, reducirá la asignación de GPU y congelará temporizadores del sistema para evitar picos sorpresa de CPU. Todo con un solo click.

![Servidor optimizado corriendo en Trixie](images/miserverT.png)

> [!CAUTION]
> **Recomendación de Sistema Operativo:** Estas optimizaciones de hardware modifican el núcleo de Linux, los servicios Swap, Systemd y la tabla de particiones `fstab`. Han sido **estrictamente validadas y desarrolladas en Raspberry Pi OS 13 Trixie (32-bit lite)**, que es el entorno nativo de desarrollo del proyecto. Desconocemos la estabilidad o si los comandos difieren en otras versiones de Debian (Bullseye/Bookworm) o en arquitecturas de 64-bits. Te sugerimos encarecidamente utilizar Raspbian Trixie para una experiencia libre de errores.

---

## 📝 Nota de Hardware: Configuración del Bus USB - Raspberry Pi Zero 1W

Documentación de los cambios aplicados en el sistema para corregir el problema de detección de periféricos USB en el arranque y forzar el puerto Micro-USB en modo Host en Debian Trixie Lite.

### 1. Diagnóstico de Hardware y Solución

Tras agotar las pruebas de software, se determinó lo siguiente respecto a los componentes físicos:

* **Alimentación:** El cargador de pared ZTE de 5V a 2A y el cable PowerA proporcionan la energía y el blindaje necesarios para mantener la placa estable sin sufrir caídas de tensión.
* **Causa Raíz:** El adaptador físico Micro-USB OTG original estaba defectuoso o carecía de las líneas de datos internas (era un cable exclusivamente de carga). El pin 4 (ID) no estaba puenteado a tierra, lo que impedía que el procesador Broadcom abriera el canal de datos.
* **Solución Física:** Se reemplazó el adaptador por un adaptador OTG real con soporte de datos. El sistema reconoció de inmediato la memoria Kingston DTSE9 de 16GB, mapeándola exitosamente en el bus de bloques como `/dev/sda`.

### 2. Galería de Montaje

**Pre-Configuración (Cable de solo carga):**
Así estaba el montaje inicial antes de descubrir que el problema era el cable "OTG" que en realidad solo entregaba energía.
![Antes del cambio de OTG](images/preServidor.jpg)

**Montaje Actual (Operativo):**
Así se encuentra operando actualmente nuestro servidor Nube-Zero, con el almacenamiento externo debidamente reconocido.
![Servidor Nube-Zero funcionando](images/miServidor.jpg)

### 3. Modificaciones en el Firmware (`/boot/firmware/config.txt`)

Se añadieron las siguientes líneas al final del archivo para obligar al kernel a inicializar el bus USB correctamente desde el encendido, evitando que el puerto se quede en modo de espera pasivo (ahorro de energía) o adivinando el rol del puerto OTG:

```text
[all]
# Cambia el controlador USB nativo al modo clásico de Broadcom forzado a Host
dtoverlay=dwc_otg,dr_mode=host

# Añade un retraso de 5 segundos al arranque para estabilizar el voltaje del bus eléctrico
boot_delay=5
```

### 4. Estado Actual del Sistema

El bus de almacenamiento ya detecta correctamente el hardware montado:

```bash
# Comando de verificación:
lsblk

# Salida esperada:
sda           8:0    1 14.5G  0 disk
├─sda1        8:1    1  200M  0 part
└─sda2        8:2    1 14.3G  0 part
```
