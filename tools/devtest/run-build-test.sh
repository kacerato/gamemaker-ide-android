#!/bin/bash
# Compila o APK, instala, abre o projeto de teste, aperta F5 e grava o log do Igor.
# Uso: bash tools/devtest/run-build-test.sh <nome>   (saida em logs/<nome>.txt e logs/<nome>.png)
set -u
export MSYS_NO_PATHCONV=1
cd "$(dirname "$0")/../.."
NAME=${1:-igor}
ADB="$LOCALAPPDATA/Android/Sdk/platform-tools/adb.exe"
export ANDROID_SERIAL=${ANDROID_SERIAL:-$("$ADB" devices | awk 'NR>1 && $2=="device"{print $1; exit}')}
PROJ=${PROJ:-/storage/emulated/0/GameMakerProjects/Idle Game Template/Idle Game Template.yyp}
if [ "${SKIP_BUILD:-0}" != 1 ]; then
  (cd host && dotnet build -c Release 2>&1 | grep -E " error " | head -5)
  "$ADB" install -r host/bin/Release/net8.0-android/android-arm64/io.gmide.android-Signed.apk | tail -1
fi
"$ADB" shell am force-stop io.gmide.android
"$ADB" shell am start -n io.gmide.android/io.gmide.GmIdeActivity --es project "'$PROJ'" >/dev/null
sleep ${BOOT_WAIT:-60}
"$ADB" logcat -c
"$ADB" shell input keyevent 135   # F5
sleep ${RUN_WAIT:-60}
"$ADB" logcat -d -v brief -s GmIde.Igor:* GmIde.Build:* > "logs/$NAME.txt"
# So captura a tela se a IDE estiver em primeiro plano (nao fotografar outros apps).
if "$ADB" shell dumpsys activity activities | grep topResumedActivity | grep -q io.gmide; then
  "$ADB" exec-out screencap -p > "logs/$NAME.png"
else
  echo "IDE nao esta em primeiro plano; sem captura"; rm -f "logs/$NAME.png"
fi
wc -l "logs/$NAME.txt"
