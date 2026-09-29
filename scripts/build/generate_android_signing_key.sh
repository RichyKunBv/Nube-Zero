#!/usr/bin/env bash
set -euo pipefail

KEYSTORE_DIR="$HOME/.nubezero/keys"
KEYSTORE_PATH="$KEYSTORE_DIR/nubezero-release.keystore"
KEY_ALIAS="nubezero-release"
umask 077

if ! command -v keytool >/dev/null 2>&1; then
    echo "Error: instala Java (keytool) y vuelve a ejecutar este script." >&2
    exit 1
fi

if [[ -e "$KEYSTORE_PATH" ]]; then
    echo "Error: ya existe $KEYSTORE_PATH; no se sobrescribió." >&2
    exit 1
fi

mkdir -p "$KEYSTORE_DIR"

echo "Se guardará una clave privada en: $KEYSTORE_PATH"
echo "No pierdas el keystore ni las contraseñas: Android los requiere para actualizar la app."
echo "Usa el alias '$KEY_ALIAS' y, al pedir la contraseña de la clave, pulsa Enter para reutilizar la del keystore."

keytool -genkeypair -v \
    -keystore "$KEYSTORE_PATH" \
    -alias "$KEY_ALIAS" \
    -keyalg RSA \
    -keysize 2048 \
    -validity 10000

chmod 600 "$KEYSTORE_PATH"

if command -v pbcopy >/dev/null 2>&1; then
    base64 -i "$KEYSTORE_PATH" | tr -d '\n' | pbcopy
    echo "ANDROID_KEYSTORE_BASE64 quedó copiado al portapapeles para pegarlo directamente en GitHub Secrets."
else
    echo "Codifica el archivo en Base64 para el secreto ANDROID_KEYSTORE_BASE64: $KEYSTORE_PATH"
fi

echo "Alias para ANDROID_KEY_ALIAS: $KEY_ALIAS"
echo "La contraseña del keystore va en ANDROID_KEYSTORE_PASSWORD y ANDROID_KEY_PASSWORD."