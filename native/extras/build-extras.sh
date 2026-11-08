#!/usr/bin/env bash
# Builds/fetches the extra Android arm64 native libs for the GameMaker IDE port.
# Output: native/out-extras/arm64-v8a/. Run from Git Bash: bash native/extras/build-extras.sh
# See README.md in this folder for versions, URLs and the DllImport-name mapping.
set -euo pipefail
export MSYS_NO_PATHCONV=1

HERE="$(cygpath -m "$(cd "$(dirname "$0")" && pwd)")"
OUT="$HERE/../out-extras/arm64-v8a"
WORK="${WORK:-$HERE/.work}"
NDK="${ANDROID_NDK_HOME:-$LOCALAPPDATA/Android/Sdk/ndk/27.1.12297006}"
BIN="$NDK/toolchains/llvm/prebuilt/windows-x86_64/bin"
CC="$BIN/clang.exe --target=aarch64-linux-android26"
NM="$BIN/llvm-nm.exe"
GM="${GM_DIR:-C:/Program Files/GameMaker-LTS2026}"

HB_VER=8.3.1.1
HB_URL="https://www.nuget.org/api/v2/package/HarfBuzzSharp.NativeAssets.Android/$HB_VER"

mkdir -p "$OUT" "$WORK"
CFLAGS="-O2 -fPIC -fvisibility=hidden -Wall -Wextra"
LDFLAGS="-shared -Wl,-z,max-page-size=16384 -Wl,--no-undefined -Wl,--build-id=sha1"

# 1. HarfBuzzSharp (official NuGet package, signed by Microsoft + nuget.org)
if [ ! -f "$WORK/hb/hb.nupkg" ]; then
  mkdir -p "$WORK/hb"
  curl -sSfL -o "$WORK/hb/hb.nupkg" "$HB_URL"
fi
dotnet nuget verify --all "$WORK/hb/hb.nupkg" >/dev/null
rm -rf "$WORK/hb/x" && mkdir -p "$WORK/hb/x"
unzip -q -o "$WORK/hb/hb.nupkg" 'runtimes/android-arm64/native/libHarfBuzzSharp.so' -d "$WORK/hb/x"
cp "$WORK/hb/x/runtimes/android-arm64/native/libHarfBuzzSharp.so" "$OUT/libHarfBuzzSharp.so"

# 2. IDENativeLib stub
$CC $CFLAGS $LDFLAGS -Wl,-soname,libIDENativeLib.so -o "$OUT/libIDENativeLib.so" "$HERE/src/IDENativeLib.c" -llog

# 3. tinyfiledialogs replacement (SDL message boxes, dialogs = cancel)
$CC $CFLAGS $LDFLAGS -Wl,-soname,libtinyfiledialogs.so -o "$OUT/libtinyfiledialogs.so" "$HERE/src/tinyfiledialogs.c" -llog -ldl

# Verify: every P/Invoke entry point of the consuming assembly is exported.
verify() { # <so> <module name in DllImport, lowercase> <assembly>
  local so="$1" mod="$2" asm="$3" miss
  "$NM" -D --defined-only "$OUT/$so" | awk '{print $3}' | sort -u > "$WORK/exp.txt"
  pwsh -NoProfile -File "$HERE/tools/list-pinvokes.ps1" -Dir "$GM" -Filter "$asm" \
    | awk -F'\t' -v m="$mod" 'tolower($2)==m{print $3}' | sort -u > "$WORK/imp.txt"
  miss="$(comm -23 "$WORK/imp.txt" "$WORK/exp.txt")"
  if [ -n "$miss" ]; then echo "FAIL $so: missing $miss" >&2; return 1; fi
  echo "OK   $so: $(wc -l < "$WORK/imp.txt") imports of $asm resolved"
}
verify libHarfBuzzSharp.so  libharfbuzzsharp HarfBuzzSharp.dll
verify libIDENativeLib.so   idenativelib     DebugInfo.dll
verify libtinyfiledialogs.so tinyfiledialogs OSCore.dll
( cd "$OUT" && sha256sum *.so )
