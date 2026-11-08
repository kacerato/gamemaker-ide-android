#!/usr/bin/env bash
# Compila o stub gmexec (native/gmexec/gmexec.c) como EXECUTAVEL PIE arm64 e o grava
# como native/out-gmexec/arm64-v8a/libgmexec.so (nome de lib para o APK extrair na
# nativeLibraryDir, a unica pasta onde apps podem executar arquivos).
# Uso: bash native/gmexec/build.sh            (Android arm64, NDK 27.1, API 26)
#      bash native/gmexec/build.sh --pc       (tambem gera gmexec.exe para o teste do PC,
#                                              tools/build-test/stub-test; precisa do gcc MinGW)
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
NDK="${ANDROID_NDK:-$LOCALAPPDATA/Android/Sdk/ndk/27.1.12297006}"
API=26
OUT="$ROOT/native/out-gmexec/arm64-v8a"
mkdir -p "$OUT"
w() { cygpath -m "$1" 2>/dev/null || echo "$1"; } # clang do Windows nao entende /c/...

CC="$NDK/toolchains/llvm/prebuilt/windows-x86_64/bin/aarch64-linux-android$API-clang.cmd"
[ -f "$CC" ] || CC="$NDK/toolchains/llvm/prebuilt/linux-x86_64/bin/aarch64-linux-android$API-clang"
# -fPIE -pie: executavel (Android exige PIE). 16 KB de pagina para aparelhos novos.
"$CC" -O2 -Wall -fPIE -pie -Wl,-z,max-page-size=16384 -Wl,--gc-sections -s \
  -o "$(w "$OUT/libgmexec.so")" "$(w "$HERE/gmexec.c")"
echo "ok: $OUT/libgmexec.so"

if [ "${1:-}" = "--pc" ]; then
  PCOUT="$ROOT/tools/build-test/stub-test/bin"
  mkdir -p "$PCOUT"
  gcc -O2 -Wall -o "$PCOUT/gmexec.exe" "$HERE/gmexec.c" -lws2_32
  echo "ok: $PCOUT/gmexec.exe"
fi
