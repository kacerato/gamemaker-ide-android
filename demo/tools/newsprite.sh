#!/bin/bash
# newsprite.sh NOME arquivo.png  (escala reduzida atual)
export MSYS_NO_PATHCONV=1; ADB="$LOCALAPPDATA/Android/Sdk/platform-tools/adb.exe"; d="$(dirname "$0")"
"$d/g.sh" tap 2582 274; sleep 1.5
"$d/g.sh" tap 2318 910; sleep 0.5; "$d/g.sh" tap 2404 1040; sleep 2.5
"$d/g.sh" key 111; sleep 0.5
"$d/g.sh" tap 526 408; sleep 1; "$ADB" shell input keycombination 113 29; "$d/g.sh" text "$1"; "$ADB" shell input keyevent 66; sleep 0.4; "$ADB" shell input keyevent 4; sleep 1
"$d/g.sh" tap 436 862; sleep 4
for i in 1 2 3 4; do p=$("$d/ui.sh" find "$2"); [ -n "$p" ] && break; "$ADB" shell input swipe 640 2200 640 1400 400; sleep 1; done
set -- $p; "$ADB" shell input tap $1 $(( $2 - 200 )); sleep 4
"$d/g.sh" fg
