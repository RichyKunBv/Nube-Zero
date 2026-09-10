!include "MUI2.nsh"

Name "NubeZero"
OutFile "${OUTFILE}"

; Por defecto instalará en Program Files (o Program Files x86 si fuera 32 bits)
InstallDir "$PROGRAMFILES64\NubeZero"

; Pedir privilegios de administrador para instalar en Archivos de Programa
RequestExecutionLevel admin

!define MUI_ABORTWARNING

; Páginas
!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_WELCOME
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH

; Idiomas
!insertmacro MUI_LANGUAGE "Spanish"
!insertmacro MUI_LANGUAGE "English"

Section "Instalación" SecInstall
    SetOutPath "$INSTDIR"
    
    ; Copiar todos los archivos desde la carpeta de publicación (se pasa por comando)
    File /r "${PUBLISH_DIR}\*"
    
    ; Crear desinstalador
    WriteUninstaller "$INSTDIR\Uninstall.exe"
    
    ; Crear acceso directo en el Menú Inicio
    CreateDirectory "$SMPROGRAMS\NubeZero"
    CreateShortcut "$SMPROGRAMS\NubeZero\NubeZero.lnk" "$INSTDIR\NubeZero.exe"
    
    ; Crear acceso directo en el Escritorio
    CreateShortcut "$DESKTOP\NubeZero.lnk" "$INSTDIR\NubeZero.exe"
    
    ; Registrar para desinstalar en Panel de Control
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\NubeZero" "DisplayName" "NubeZero"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\NubeZero" "UninstallString" '"$INSTDIR\Uninstall.exe"'
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\NubeZero" "DisplayIcon" "$INSTDIR\NubeZero.exe"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\NubeZero" "Publisher" "EsMeSolutions"
    WriteRegStr HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\NubeZero" "DisplayVersion" "${VERSION}"
SectionEnd

Section "Uninstall"
    ; Eliminar archivos instalados (usamos /r para asegurar que borra todo el contenido)
    RMDir /r "$INSTDIR"
    
    ; Eliminar accesos directos
    Delete "$SMPROGRAMS\NubeZero\NubeZero.lnk"
    RMDir "$SMPROGRAMS\NubeZero"
    Delete "$DESKTOP\NubeZero.lnk"
    
    ; Eliminar registro
    DeleteRegKey HKLM "Software\Microsoft\Windows\CurrentVersion\Uninstall\NubeZero"
SectionEnd
