#!/usr/bin/env bash
# Gera libMagick.Native-Q8-arm64.so (Android arm64-v8a, API 26) equivalente ao
# Magick.Native-Q8-arm64.dll que o Magick.NET-Q8-AnyCPU 14.9.1 espera.
#
# Uso (Git Bash, a partir de qualquer pasta):
#   bash native/magick/build-magick.sh            # tudo
#   bash native/magick/build-magick.sh deps       # so os delegates
#   bash native/magick/build-magick.sh im         # so ImageMagick (configure + compilacao)
#   bash native/magick/build-magick.sh im-build   # so recompila o ImageMagick (sem refazer o configure)
#   bash native/magick/build-magick.sh native     # so o .so final (Magick.Native + link)
#   bash native/magick/build-magick.sh verify     # so a verificacao de ABI
#
# Requer: NDK 27.1, cmake+ninja+pkgconf (WinLibs) no PATH, Git Bash, fontes em native/src/magick
# (baixe com native/magick/fetch-sources.sh).
set -euo pipefail
export MSYS_NO_PATHCONV=1

HERE="$(cd "$(dirname "$0")" && pwd)"
NATIVE="$(cd "$HERE/.." && pwd)"
m() { cygpath -m "$1" 2>/dev/null || echo "$1"; }
SRC="$(m "$NATIVE/src/magick")"
BUILD="$(m "$NATIVE/build-magick")"
PREFIX="$BUILD/prefix"
OUT="$(m "$NATIVE/out-magick/arm64-v8a")"
NDK="$(m "${ANDROID_NDK:-$LOCALAPPDATA/Android/Sdk/ndk/27.1.12297006}")"
API=26
ABI=arm64-v8a
TRIPLE=aarch64-linux-android
JOBS="${JOBS:-$(nproc)}"
TC="$NDK/toolchains/llvm/prebuilt/windows-x86_64"
export CC="$TC/bin/clang.exe"
export CXX="$TC/bin/clang++.exe"
export AR="$TC/bin/llvm-ar.exe"
export RANLIB="$TC/bin/llvm-ranlib.exe"
export NM="$TC/bin/llvm-nm.exe"
export STRIP="$TC/bin/llvm-strip.exe"
TARGET_FLAGS="--target=$TRIPLE$API"
OPT_FLAGS="-O3 -fPIC -DNDEBUG -ffunction-sections -fdata-sections"

STAGE="${1:-all}"
mkdir -p "$BUILD" "$PREFIX/include" "$PREFIX/lib/pkgconfig" "$OUT"

untar() { # $1=arquivo em $SRC/dl  $2=pasta de destino em $BUILD/src
  local dst="$BUILD/src/$2"
  if [ ! -d "$dst" ]; then
    rm -rf "$dst.tmp"; mkdir -p "$dst.tmp"
    tar --force-local -xzf "$SRC/dl/$1" -C "$dst.tmp" --strip-components=1
    mv "$dst.tmp" "$dst"
  fi
  echo "$dst"
}

cmake_android() { # $1=nome  $2=fonte  resto=flags
  local name="$1" src="$2"; shift 2
  echo "=== $name"
  cmake -S "$src" -B "$BUILD/$name" -G Ninja \
    -DCMAKE_TOOLCHAIN_FILE="$NDK/build/cmake/android.toolchain.cmake" \
    -DANDROID_ABI=$ABI -DANDROID_PLATFORM=android-$API \
    -DCMAKE_BUILD_TYPE=Release -DCMAKE_POLICY_VERSION_MINIMUM=3.5 \
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON -DBUILD_SHARED_LIBS=OFF \
    -DCMAKE_INSTALL_PREFIX="$PREFIX" -DCMAKE_PREFIX_PATH="$PREFIX" \
    -DCMAKE_FIND_ROOT_PATH="$PREFIX" -DCMAKE_INSTALL_LIBDIR=lib \
    "$@" > "$BUILD/$name.configure.log" 2>&1 || { tail -40 "$BUILD/$name.configure.log"; exit 1; }
  cmake --build "$BUILD/$name" -j "$JOBS" --target install > "$BUILD/$name.build.log" 2>&1 \
    || { tail -40 "$BUILD/$name.build.log"; exit 1; }
}

cc_static() { # $1=nome da lib  $2=pasta obj  $3=flags  resto=fontes
  local lib="$1" obj="$2" flags="$3"; shift 3
  rm -rf "$obj"; mkdir -p "$obj"
  local objs=()
  for f in "$@"; do
    local o="$obj/$(basename "${f%.c}").o"
    "$CC" $TARGET_FLAGS $OPT_FLAGS $flags -c "$f" -o "$o" &
    objs+=("$o")
    while [ "$(jobs -rp | wc -l)" -ge "$JOBS" ]; do wait -n; done
  done
  wait
  "$AR" rcs "$PREFIX/lib/$lib" "${objs[@]}"
}

build_deps() {
  # zlib 1.3.1
  local s; s=$(untar zlib-1.3.1.tar.gz zlib)
  cmake_android zlib "$s" -DZLIB_BUILD_EXAMPLES=OFF
  rm -f "$PREFIX"/lib/libz.so*  # so o estatico
  cp "$PREFIX/share/pkgconfig/zlib.pc" "$PREFIX/lib/pkgconfig/"

  # bzip2 1.0.8 (sem build system cross: compila direto)
  s=$(untar bzip2-1.0.8.tar.gz bzip2)
  echo "=== bzip2"
  cc_static libbz2.a "$BUILD/bzip2" "-D_FILE_OFFSET_BITS=64" \
    "$s"/{blocksort,huffman,crctable,randtable,compress,decompress,bzlib}.c
  cp "$s/bzlib.h" "$PREFIX/include/"

  # xz / liblzma 5.8.1
  s=$(untar xz-5.8.1.tar.gz xz)
  cmake_android xz "$s" -DXZ_TOOL_XZ=OFF -DXZ_TOOL_XZDEC=OFF -DXZ_TOOL_LZMADEC=OFF \
    -DXZ_TOOL_LZMAINFO=OFF -DXZ_TOOL_SCRIPTS=OFF -DXZ_TOOL_SYMLINKS=OFF -DXZ_DOC=OFF \
    -DXZ_NLS=OFF -DBUILD_TESTING=OFF

  # libpng 1.6.50
  s=$(untar libpng-1.6.50.tar.gz libpng)
  cmake_android libpng "$s" -DPNG_SHARED=OFF -DPNG_STATIC=ON -DPNG_TESTS=OFF -DPNG_TOOLS=OFF \
    -DPNG_FRAMEWORK=OFF -DZLIB_ROOT="$PREFIX" -DZLIB_LIBRARY="$PREFIX/lib/libz.a" \
    -DZLIB_INCLUDE_DIR="$PREFIX/include"
  # o CMake do libpng nao gera .pc quando o host e Windows
  cat > "$PREFIX/lib/pkgconfig/libpng.pc" <<EOF2
prefix=$PREFIX
libdir=\${prefix}/lib
includedir=\${prefix}/include/libpng16
Name: libpng
Description: Loads and saves PNG files
Version: 1.6.50
Requires.private: zlib
Libs: -L\${libdir} -lpng16
Libs.private: -lm
Cflags: -I\${includedir}
EOF2
  cp "$PREFIX/lib/pkgconfig/libpng.pc" "$PREFIX/lib/pkgconfig/libpng16.pc"

  # libjpeg-turbo 3.1.2
  s=$(untar libjpeg-turbo-3.1.2.tar.gz libjpeg-turbo)
  cmake_android libjpeg-turbo "$s" -DENABLE_SHARED=OFF -DENABLE_STATIC=ON -DWITH_TURBOJPEG=OFF \
    -DWITH_TOOLS=OFF -DWITH_TESTS=OFF -DREQUIRE_SIMD=OFF

  # freetype 2.14.1 (so zlib; sem png/bzip2/brotli/harfbuzz)
  s=$(untar freetype-2.14.1.tar.gz freetype)
  cmake_android freetype "$s" -DFT_DISABLE_HARFBUZZ=ON -DFT_DISABLE_BROTLI=ON \
    -DFT_DISABLE_BZIP2=ON -DFT_DISABLE_PNG=ON -DFT_REQUIRE_ZLIB=ON \
    -DZLIB_LIBRARY="$PREFIX/lib/libz.a" -DZLIB_INCLUDE_DIR="$PREFIX/include"

  # lcms2 2.17 (sem CMake: compila direto)
  s=$(untar lcms2-2.17.tar.gz lcms2)
  echo "=== lcms2"
  cc_static liblcms2.a "$BUILD/lcms2" "-I$s/include -DHAVE_FUNC_ATTRIBUTE_VISIBILITY=1 -DHasTHREADS=1" "$s"/src/*.c
  cp "$s"/include/lcms2.h "$s"/include/lcms2_plugin.h "$PREFIX/include/"
  cat > "$PREFIX/lib/pkgconfig/lcms2.pc" <<EOF
prefix=$PREFIX
libdir=\${prefix}/lib
includedir=\${prefix}/include
Name: lcms2
Description: LCMS Color Management Library
Version: 2.17
Libs: -L\${libdir} -llcms2
Libs.private: -lm -lpthread
Cflags: -I\${includedir}
EOF

  # libxml2 2.15.1 (sem python, sem iconv externo, sem icu)
  s=$(untar libxml2-2.15.1.tar.gz libxml2)
  cmake_android libxml2 "$s" -DLIBXML2_WITH_PYTHON=OFF -DLIBXML2_WITH_ICONV=OFF \
    -DLIBXML2_WITH_ICU=OFF -DLIBXML2_WITH_LZMA=OFF -DLIBXML2_WITH_ZLIB=OFF \
    -DLIBXML2_WITH_PROGRAMS=OFF -DLIBXML2_WITH_TESTS=OFF -DLIBXML2_WITH_DOCS=OFF \
    -DLIBXML2_WITH_HTTP=OFF -DLIBXML2_WITH_MODULES=OFF -DLIBXML2_WITH_READLINE=OFF \
    -DLIBXML2_WITH_HISTORY=OFF

  # libwebp 1.6.0
  s=$(untar libwebp-1.6.0.tar.gz libwebp)
  cmake_android libwebp "$s" -DWEBP_BUILD_ANIM_UTILS=OFF -DWEBP_BUILD_CWEBP=OFF \
    -DWEBP_BUILD_DWEBP=OFF -DWEBP_BUILD_GIF2WEBP=OFF -DWEBP_BUILD_IMG2WEBP=OFF \
    -DWEBP_BUILD_VWEBP=OFF -DWEBP_BUILD_WEBPINFO=OFF -DWEBP_BUILD_WEBPMUX=OFF \
    -DWEBP_BUILD_EXTRAS=OFF -DWEBP_BUILD_LIBWEBPMUX=ON

  # libtiff 4.7.1 (zlib, jpeg, lzma; sem webp/zstd/deflate/jbig/lerc)
  s=$(untar tiff-4.7.1.tar.gz tiff)
  cmake_android tiff "$s" -Dtiff-tools=OFF -Dtiff-tests=OFF -Dtiff-contrib=OFF -Dtiff-docs=OFF \
    -Dtiff-deprecated=OFF -Dcxx=OFF -Dzlib=ON -Djpeg=ON -Dlzma=ON -Dwebp=OFF -Dzstd=OFF \
    -Dlibdeflate=OFF -Djbig=OFF -Dlerc=OFF -Dpixarlog=ON -Dold-jpeg=ON -Djpeg12=OFF \
    -DZLIB_LIBRARY="$PREFIX/lib/libz.a" -DZLIB_INCLUDE_DIR="$PREFIX/include" \
    -DJPEG_LIBRARY="$PREFIX/lib/libjpeg.a" -DJPEG_INCLUDE_DIR="$PREFIX/include" \
    -DLIBLZMA_LIBRARY="$PREFIX/lib/liblzma.a" -DLIBLZMA_INCLUDE_DIR="$PREFIX/include"

  ls -la "$PREFIX/lib"/*.a
}

am_sources() { # $1=Makefile.am  $2=variavel -> lista de .c
  awk -v var="$2" '
    $0 ~ "^"var"[ \t]*=" { on=1; sub("^"var"[ \t]*=", "") }
    on { for (i=1;i<=NF;i++) if ($i ~ /\.c$/) print $i; if ($0 !~ /\\[ \t]*$/) on=0 }
  ' "$1"
}

# ImageMagick 7.1.2-8 (estatico, Q8, sem HDRI, sem OpenMP, mascaras de canal 64 bits,
# mesmas opcoes do build/shared/build.imagemagick.sh do Magick.Native).
# O configure roda so para gerar config.h/magick-baseconfig.h; a compilacao e feita por um
# build.ninja gerado aqui (libtool+make do autotools no Windows sao frageis).
IM_DELEGATE_CODERS="coders/jpeg.c coders/png.c coders/ept.c coders/tiff.c coders/webp.c"
build_imagemagick() {
  local im="$BUILD/ImageMagick"
  echo "=== ImageMagick: copia da fonte"
  rm -rf "$im"; mkdir -p "$im"
  (cd "$SRC/ImageMagick" && tar --exclude=.git -cf - .) | (cd "$im" && tar -xf -)

  echo "=== ImageMagick: configure (lento no Git Bash)"
  (
    cd "$im"
    export CC="$CC $TARGET_FLAGS" CXX="$CXX $TARGET_FLAGS"
    export CFLAGS="$OPT_FLAGS" CXXFLAGS="$OPT_FLAGS"
    export CPPFLAGS="-I$PREFIX/include" LDFLAGS="-L$PREFIX/lib"
    export PKG_CONFIG="pkgconf --dont-define-prefix" PKG_CONFIG_LIBDIR="$PREFIX/lib/pkgconfig" PKG_CONFIG_PATH=""
    ./configure --host=$TRIPLE --build=x86_64-pc-msys --prefix=/usr/local \
      --disable-shared --enable-static --disable-opencl --disable-dpc --disable-assert \
      --disable-deprecated --without-magick-plus-plus --without-utilities --disable-docs \
      --without-x --without-perl --without-python --without-modules \
      --with-quantum-depth=8 --enable-hdri=no --enable-64bit-channel-masks --disable-openmp \
      --with-zlib --with-bzlib --with-lzma --with-png --with-jpeg --with-tiff --with-webp \
      --with-freetype --with-lcms --with-xml \
      --without-rsvg --without-jxl --without-heic --without-raw --without-openexr \
      --without-fontconfig --without-pango --without-raqm --without-lqr --without-djvu \
      --without-fftw --without-gslib --without-gvc --without-wmf --without-openjp2 \
      --without-zstd --without-zip --without-jbig --without-dps --without-flif --without-fpx \
      --without-uhdr --without-tcmalloc --without-jemalloc --without-umem --without-autotrace \
      --without-dmr --without-utilities --disable-dependency-tracking
  ) > "$BUILD/im.configure.log" 2>&1 || { tail -40 "$BUILD/im.configure.log"; exit 1; }
  grep -E "^  (Channel mask|Quantum|HDRI|OpenMP)|^ *(BZLIB|FreeType|JPEG v1|LCMS|LZMA|PNG|TIFF|WEBP|XML|ZLIB) " \
    "$BUILD/im.configure.log" || true

}

compile_imagemagick() {
  local im="$BUILD/ImageMagick"
  echo "=== ImageMagick: compilacao"
  local srcs=()
  srcs+=($(am_sources "$im/MagickCore/Makefile.am" MAGICKCORE_BASE_SRCS))
  srcs+=($(am_sources "$im/MagickWand/Makefile.am" MAGICKWAND_SOURCES))
  srcs+=($(am_sources "$im/coders/Makefile.am" MAGICKCORE_CODER_SRCS))
  srcs+=($(am_sources "$im/filters/Makefile.am" MAGICKCORE_FILTER_SRCS))
  srcs+=($IM_DELEGATE_CODERS)
  local nj="$im/build.ninja"
  local cflags="$TARGET_FLAGS $OPT_FLAGS -fvisibility=hidden -I$im -I$PREFIX/include -I$PREFIX/include/freetype2 -I$PREFIX/include/libxml2 -I$PREFIX/include/webp -DHAVE_CONFIG_H -DMAGICKCORE_HDRI_ENABLE=0 -DMAGICKCORE_QUANTUM_DEPTH=8 -DMAGICKCORE_CHANNEL_MASK_DEPTH=64 -D_REENTRANT -D_FILE_OFFSET_BITS=64 -D_LARGEFILE_SOURCE -Wno-unused -Wno-deprecated-declarations -Wno-fixed-enum-extension"
  {
    echo "cc = $CC"
    echo "ar = $AR"
    echo "cflags = $cflags"
    echo "rule cc"
    echo "  command = \$cc \$cflags -MD -MF \$out.d -c \$in -o \$out"
    echo "  deps = gcc"
    echo "  depfile = \$out.d"
    echo "rule ar"
    echo "  command = \$ar rcs \$out @\$out.rsp"
    echo "  rspfile = \$out.rsp"
    echo "  rspfile_content = \$in"
    local objs=()
    for s in $(printf '%s\n' "${srcs[@]}" | sort -u); do
      local o="obj/${s%.c}.o"
      echo "build $o: cc $s"
      objs+=("$o")
    done
    echo "build libMagickAll.a: ar ${objs[*]}"
  } > "$nj"
  ninja -C "$im" -j "$JOBS" > "$BUILD/im.build.log" 2>&1 || { grep -m20 -B2 -A8 "error:" "$BUILD/im.build.log"; exit 1; }
  cp "$im/libMagickAll.a" "$PREFIX/lib/libMagickAll-7.Q8.a"
  echo "ImageMagick: $(printf '%s\n' "${srcs[@]}" | sort -u | wc -l) fontes"
}

# Magick.Native (src/Magick.Native/*.c compilados como C++, como no CMakeLists oficial) +
# ligacao de tudo num unico .so. So os simbolos MAGICK_NATIVE_EXPORT ficam exportados.
SO_NAME=libMagick.Native-Q8-arm64.so
build_native() {
  local im="$BUILD/ImageMagick"
  local mn="$SRC/Magick.Native/src/Magick.Native"
  local nb="$BUILD/Magick.Native"
  rm -rf "$nb"; mkdir -p "$nb"
  local nj="$nb/build.ninja"
  local cxxflags="$TARGET_FLAGS $OPT_FLAGS -x c++ -std=gnu++17 -fvisibility=hidden -DMAGICK_NATIVE_LINUX -DMAGICKCORE_QUANTUM_DEPTH=8 -DMAGICKCORE_HDRI_ENABLE=0 -DMAGICKCORE_CHANNEL_MASK_DEPTH=64 -I$mn -I$im -I$PREFIX/include -Wno-unused-function -Wno-deprecated-declarations -Wno-fixed-enum-extension -Wno-deprecated"
  echo "=== Magick.Native: compilacao"
  {
    echo "cxx = $CXX"
    echo "cxxflags = $cxxflags"
    echo "rule cxx"
    echo "  command = \$cxx \$cxxflags -MD -MF \$out.d -c \$in -o \$out"
    echo "  deps = gcc"
    echo "  depfile = \$out.d"
    (cd "$mn" && find . -name '*.c' -not -path '*/CMakeFiles/*' | sed 's|^\./||' | sort) | while read -r s; do
      echo "build obj/${s%.c}.o: cxx ${mn/:/\$:}/$s"
    done
  } > "$nj"
  ninja -C "$nb" -j "$JOBS" > "$BUILD/native.build.log" 2>&1 || { grep -m20 -B2 -A8 "error:" "$BUILD/native.build.log"; exit 1; }

  # lista de exportacao = simbolos GLOBAL DEFAULT definidos nos objetos do Magick.Native
  find "$nb/obj" -name '*.o' > "$nb/objs.txt"
  "$TC/bin/llvm-readelf.exe" --syms $(cat "$nb/objs.txt") | tr -d '\r' \
    | awk '$5=="GLOBAL" && $6=="DEFAULT" && $7!="UND" && ($4=="FUNC" || $4=="OBJECT") {print $8}' \
    | sort -u > "$nb/exports.txt"
  { echo "{ global:"; sed 's/$/;/' "$nb/exports.txt"; echo "local: *; };"; } > "$nb/exports.map"
  echo "Magick.Native: $(wc -l < "$nb/exports.txt") simbolos exportados"

  echo "=== link $SO_NAME"
  local L="$PREFIX/lib"
  "$CXX" $TARGET_FLAGS -shared -o "$nb/$SO_NAME" -Wl,-soname,$SO_NAME \
    $(cat "$nb/objs.txt") \
    "$L/libMagickAll-7.Q8.a" "$L/libtiff.a" \
    "$L/libwebpmux.a" "$L/libwebpdemux.a" "$L/libwebp.a" "$L/libsharpyuv.a" "$L/libcpufeatures-webp.a" \
    "$L/libpng16.a" "$L/libjpeg.a" "$L/libfreetype.a" "$L/liblcms2.a" "$L/libxml2.a" \
    "$L/liblzma.a" "$L/libbz2.a" "$L/libz.a" \
    -static-libstdc++ -lm -ldl -llog \
    -Wl,--version-script="$nb/exports.map" -Wl,--gc-sections -Wl,--no-undefined \
    -Wl,-z,max-page-size=16384 -Wl,--build-id=sha1 -s \
    > "$BUILD/link.log" 2>&1 || { head -60 "$BUILD/link.log"; exit 1; }
  cp "$nb/$SO_NAME" "$OUT/$SO_NAME"
  ls -la "$OUT/$SO_NAME"
}

# Compara os P/Invokes do Magick.NET-Q8-AnyCPU.dll 14.9.1 (lista em magick/abi-expected.txt,
# extraida do ImplMap do assembly) com o que o .so exporta.
verify_abi() {
  local exp="$HERE/abi-expected.txt" got="$BUILD/abi-exported.txt"
  "$NM" -D --defined-only "$OUT/$SO_NAME" | tr -d '\r' | awk '{print $3}' | sort -u > "$got"
  local missing; missing=$(comm -23 <(tr -d '\r' < "$exp" | sort -u) "$got")
  echo "esperados: $(grep -c . "$exp")  exportados: $(grep -c . "$got")  faltando: $(printf '%s' "$missing" | grep -c . || true)"
  if [ -n "$missing" ]; then echo "$missing"; return 1; fi
  echo "dependencias dinamicas:"; "$TC/bin/llvm-readelf.exe" -d "$OUT/$SO_NAME" | grep -E "NEEDED|SONAME"
}

case "$STAGE" in
  deps) build_deps ;;
  im) build_imagemagick; compile_imagemagick ;;
  im-build) compile_imagemagick ;;
  native) build_native ;;
  verify) verify_abi ;;
  all) build_deps; build_imagemagick; compile_imagemagick; build_native; verify_abi ;;
  *) echo "estagio desconhecido: $STAGE"; exit 1 ;;
esac
