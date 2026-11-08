#!/bin/bash
# compila o host, instala, reabre a IDE e o projeto Blank Pixel Game (primeiro recente)
set -e
export MSYS_NO_PATHCONV=1
ADB="$LOCALAPPDATA/Android/Sdk/platform-tools/adb.exe"
R=/c/Users/donod/Downloads/gamemaker-ide-android
cd $R/host
[ "$1" = "java" ] && bash build-java.sh >/dev/null
out=$(dotnet build -c Release 2>&1 | grep -E " error |Compila|Falha" | sort -u)
echo "$out"; echo "$out" | grep -q "êxito" || exit 1
"$ADB" install -r "$(cygpath -w $R/host/bin/Release/net8.0-android/android-arm64/io.gmide.android-Signed.apk)" | tail -1
"$ADB" logcat -c
"$ADB" shell am start -n io.gmide.android/io.gmide.GmIdeActivity >/dev/null
sleep 30
cd $R/demo/tools; ./g.sh dtap 580 660; sleep 14; echo pronto
