#!/usr/bin/env bash
# Instala o APK, copia a IDE do GameMaker para o celular e abre o app mostrando
# o log. Requer um aparelho arm64 com depuracao USB (ou por Wi-Fi) ligada.
# Uso: bash deploy.sh            (instala + copia IDE se faltar + abre + log)
#      bash deploy.sh --no-push  (nao copia a IDE de novo)
#
# Por que uma pasta de passagem: arquivos que o adb grava em Android/data/<app>
# ficam com dono "shell" e o app nao consegue le-los. Entao a IDE vai para
# /sdcard/Download/gmide-ide e, no primeiro boot, o app importa para o
# armazenamento interno dele (Launcher.ImportIde).
set -euo pipefail
# Git Bash converte /sdcard/... em C:/Program Files/Git/sdcard/...; desliga isso.
export MSYS_NO_PATHCONV=1
HERE="$(cd "$(dirname "$0")" && pwd)"
ADB="$LOCALAPPDATA/Android/Sdk/platform-tools/adb.exe"
PKG=io.gmide.android
APK="$HERE/host/bin/Release/net8.0-android/android-arm64/$PKG-Signed.apk"
GM="/c/Program Files/GameMaker-LTS2026"
STAGING="/sdcard/Download/gmide-ide"

# Se o mesmo aparelho aparecer mais de uma vez (adb por Wi-Fi), usa o primeiro.
if [ -z "${ANDROID_SERIAL:-}" ]; then
  ANDROID_SERIAL=$("$ADB" devices | awk 'NR>1 && $2=="device" {print $1; exit}')
  export ANDROID_SERIAL
fi
"$ADB" get-state >/dev/null || { echo "Nenhum aparelho conectado ao adb."; exit 1; }
abi=$("$ADB" shell getprop ro.product.cpu.abi | tr -d '\r')
[ "$abi" = "arm64-v8a" ] || { echo "Aparelho e $abi; o APK e so arm64-v8a."; exit 1; }

echo "== instalando APK"
"$ADB" install -r "$(cygpath -w "$APK")"

# "Todos os arquivos": necessario para ler a pasta de passagem e para o seletor SAF
# devolver caminhos reais.
"$ADB" shell appops set "$PKG" MANAGE_EXTERNAL_STORAGE allow

if [ "${1:-}" != "--no-push" ] && ! "$ADB" shell "[ -f $STAGING/IDE.dll ]"; then
  echo "== copiando a IDE (~390 MB)"
  "$ADB" shell mkdir -p "$STAGING"
  "$ADB" push "$(cygpath -w "$GM")/." "$STAGING"
fi

# Substituto do VerifyDLLs da IDE (host/IntegrityCheck.cs): verifica no Windows a
# assinatura Authenticode das DLLs checadas e manda os SHA-256 para o celular.
echo "== verificando assinaturas da YoYo no PC"
MANIFEST="$HERE/logs/ide-integrity.json"
mkdir -p "$HERE/logs"
powershell -NoProfile -ExecutionPolicy Bypass -File "$(cygpath -w "$HERE/tools/make-integrity-manifest.ps1")" \
  -GmDir "$(cygpath -w "$GM")" -Out "$(cygpath -w "$MANIFEST")"
"$ADB" push "$(cygpath -w "$MANIFEST")" "$STAGING/ide-integrity.json"
# ProjectTool e Language Server (host/ToolBridge.cs): hashes das DLLs que eles carregam.
TOOLS_MANIFEST="$HERE/logs/gmide-tools-integrity.json"
powershell -NoProfile -ExecutionPolicy Bypass -File "$(cygpath -w "$HERE/tools/make-tools-manifest.ps1")" \
  -GmDir "$(cygpath -w "$GM")" -Out "$(cygpath -w "$TOOLS_MANIFEST")"
"$ADB" push "$(cygpath -w "$TOOLS_MANIFEST")" "$STAGING/gmide-tools-integrity.json"

# --- Runtime do GameMaker para o Build/Run (host/RuntimeImport.cs, docs/build-pipeline.md) ---
# Mesmo esquema da IDE: pasta publica de passagem; o app importa para
# ${system_cache_directory}/runtimes no primeiro boot. O marcador .gmide-complete vai por
# ultimo: um push interrompido nao e importado pela metade.
RT_VER=runtime-2026.0.0.23
RT_SRC="/c/ProgramData/GameMakerStudio2-LTS2026/Cache/runtimes/$RT_VER"
RT_PREFABS="/c/ProgramData/GameMakerStudio2-LTS2026/Prefabs"
RT_STAGING="/sdcard/Download/gmide-runtime"
if [ -d "$RT_SRC" ]; then
  if [ "${1:-}" != "--no-push" ] && ! "$ADB" shell "[ -f $RT_STAGING/$RT_VER/.gmide-complete ]"; then
    echo "== copiando o runtime $RT_VER (~400 MB) e os Prefabs"
    "$ADB" shell mkdir -p "$RT_STAGING/$RT_VER" "$RT_STAGING/Prefabs"
    "$ADB" push "$(cygpath -w "$RT_SRC")/." "$RT_STAGING/$RT_VER"
    [ -d "$RT_PREFABS" ] && "$ADB" push "$(cygpath -w "$RT_PREFABS")/." "$RT_STAGING/Prefabs"
    "$ADB" shell touch "$RT_STAGING/$RT_VER/.gmide-complete"
  fi
  # Substituto do WinTrust.VerifySecurity (host/RuntimeIntegrity.cs): Authenticode do
  # Igor.exe/GMAssetCompiler.dll/GMWebServer.dll verificado aqui, SHA-256 conferido no app.
  echo "== verificando assinaturas do runtime no PC"
  RT_MANIFEST="$HERE/logs/gmide-runtime-integrity.json"
  powershell -NoProfile -ExecutionPolicy Bypass -File "$(cygpath -w "$HERE/tools/make-runtime-manifest.ps1")" \
    -RuntimeDir "$(cygpath -w "$RT_SRC")" -Out "$(cygpath -w "$RT_MANIFEST")"
  "$ADB" shell mkdir -p "$RT_STAGING/$RT_VER"
  "$ADB" push "$(cygpath -w "$RT_MANIFEST")" "$RT_STAGING/$RT_VER/gmide-runtime-integrity.json"
else
  echo "== runtime $RT_SRC nao encontrado: Run/Build ficam indisponiveis no app"
fi

echo "== abrindo o app (Ctrl+C para parar o log)"
"$ADB" logcat -c
"$ADB" shell am start -n "$PKG/io.gmide.GmIdeActivity"
"$ADB" logcat -v brief GmIde:V gmhost:V SDL:V SDL/APP:V monodroid:I mono:W mono-rt:V DOTNET:V AndroidRuntime:E '*:S'
