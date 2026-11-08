#!/bin/bash
# setf.sh X Y VALOR  -> toca no campo, seleciona tudo, digita, Enter, esconde teclado
export MSYS_NO_PATHCONV=1; ADB="$LOCALAPPDATA/Android/Sdk/platform-tools/adb.exe"; d="$(dirname "$0")"
"$d/g.sh" tap "$1" "$2" || exit 3; sleep 0.9
"$ADB" shell input keycombination 113 29; sleep 0.2
"$d/g.sh" text "$3"; sleep 0.3; "$ADB" shell input keyevent 66; sleep 0.8
