#!/usr/bin/env bash
# Compila as bibliotecas nativas da IDE para Android arm64-v8a.
# Uso: bash native/build-native.sh   (a partir da raiz do projeto)
set -euo pipefail

ROOT="$(cd "$(dirname "$0")" && pwd)"
ROOT="$(cygpath -m "$ROOT" 2>/dev/null || echo "$ROOT")"  # clang do Windows nao entende /c/...
SRC="$ROOT/src"
BUILD="$ROOT/build"
OUT="$ROOT/out/arm64-v8a"
NDK="${ANDROID_NDK:-$LOCALAPPDATA/Android/Sdk/ndk/27.1.12297006}"
NDK="$(cygpath -m "$NDK" 2>/dev/null || echo "$NDK")"
ABI=arm64-v8a
API=26
JOBS="${JOBS:-$(nproc)}"

mkdir -p "$BUILD" "$OUT"

cmake_android() { # $1=nome  $2=fonte  resto=flags extras
  local name="$1" src="$2"; shift 2
  echo "=== $name"
  cmake -S "$src" -B "$BUILD/$name" -G Ninja \
    -DCMAKE_TOOLCHAIN_FILE="$NDK/build/cmake/android.toolchain.cmake" \
    -DANDROID_ABI=$ABI -DANDROID_PLATFORM=android-$API \
    -DCMAKE_BUILD_TYPE=Release -DCMAKE_POLICY_VERSION_MINIMUM=3.5 "$@"
  cmake --build "$BUILD/$name" -j "$JOBS"
}

# SDL2 2.28.3 (mesma versao que a IDE usa no Windows)
cmake_android SDL2 "$SRC/SDL2" -DSDL_SHARED=ON -DSDL_STATIC=OFF -DSDL_TEST=OFF
cp "$BUILD/SDL2/libSDL2.so" "$OUT/"

# FNA3D (OpenGL ES 3 via SDL2)
cmake_android FNA3D "$SRC/FNA3D" -DBUILD_SDL3=OFF \
  -DSDL2_INCLUDE_DIRS="$SRC/SDL2/include;$BUILD/SDL2/include;$BUILD/SDL2/include-config-release" \
  -DSDL2_LIBRARIES="$BUILD/SDL2/libSDL2.so"
cp "$BUILD/FNA3D/libFNA3D.so" "$OUT/"

# freetype 2.13.2
cmake_android freetype "$SRC/freetype" -DBUILD_SHARED_LIBS=ON \
  -DFT_DISABLE_ZLIB=ON -DFT_DISABLE_BZIP2=ON -DFT_DISABLE_PNG=ON \
  -DFT_DISABLE_HARFBUZZ=ON -DFT_DISABLE_BROTLI=ON
cp "$BUILD/freetype/libfreetype.so" "$OUT/"

# oniguruma 6.9.9 (realce de sintaxe do editor de codigo)
cmake_android oniguruma "$SRC/oniguruma" -DBUILD_SHARED_LIBS=ON -DBUILD_TEST=OFF
cp "$BUILD/oniguruma/libonig.so" "$OUT/"

# yoga 3.0.4 (layout); o CMake da yoga gera estatica, entao embrulhamos em .so
cmake_android yoga "$SRC/yoga/yoga" -DCMAKE_POSITION_INDEPENDENT_CODE=ON
CXX="$NDK/toolchains/llvm/prebuilt/windows-x86_64/bin/aarch64-linux-android$API-clang++.cmd"
"$CXX" -shared -o "$OUT/libyoga.so" -Wl,--whole-archive "$BUILD/yoga/libyogacore.a" \
  -Wl,--no-whole-archive -static-libstdc++

# stub da yyal
CC="$NDK/toolchains/llvm/prebuilt/windows-x86_64/bin/aarch64-linux-android$API-clang.cmd"
"$CC" -shared -fPIC -O2 -fvisibility=hidden -o "$OUT/libyyal.so" "$ROOT/yyal-stub/yyal_stub.c" -laaudio -llog -lm

# ponte SDL_main -> C#
"$CC" -shared -fPIC -O2 -o "$OUT/libgmhost.so" "$ROOT/gmhost/gmhost.c" -llog

echo "=== pronto"; ls -la "$OUT"
