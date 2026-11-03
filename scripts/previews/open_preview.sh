#!/bin/bash
# Abre el simulador de splash screen en tu navegador predeterminado
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
open "$DIR/splash_preview.html"

