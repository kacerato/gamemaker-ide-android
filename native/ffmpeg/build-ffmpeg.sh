#!/usr/bin/env bash
# Gera ffmpeg e ffprobe como EXECUTAVEIS PIE para Android arm64-v8a (API 24+), gravados como
#   native/out-ffmpeg/arm64-v8a/libffmpeg.so
#   native/out-ffmpeg/arm64-v8a/libffprobe.so
# (nome de lib para o Android extrair do APK na nativeLibraryDir; por dentro sao executaveis, como
# o libgmexec.so). Build minimo para o GMAssetCompiler e o editor de som: decodifica mp3/vorbis/wav/flac/aac/opus,
# codifica wav (pcm_s16le/pcm_u8), ogg (libvorbis), mp3 (libmp3lame), tiff/png (forma de onda), filtros aresample e showwavespic.
# zlib, libogg, libvorbis e LAME entram estaticos; so dependem de libc/libm/libdl do sistema.
# Sem --enable-gpl / --enable-nonfree: o resultado e LGPL (ver SOURCES.md).
#
# Uso (Git Bash, a partir de qualquer pasta):
#   bash native/ffmpeg/build-ffmpeg.sh            # tudo (deps + ffmpeg + verificacao)
#   bash native/ffmpeg/build-ffmpeg.sh deps       # so libogg, libvorbis, LAME
#   bash native/ffmpeg/build-ffmpeg.sh ffmpeg     # so FFmpeg (configure + make + copia)
#   bash native/ffmpeg/build-ffmpeg.sh ff-build   # so recompila o FFmpeg (sem refazer o configure)
#   bash native/ffmpeg/build-ffmpeg.sh verify     # so a checagem de dependencias/ELF
#   CLEAN=1 bash native/ffmpeg/build-ffmpeg.sh    # apaga native/build-ffmpeg antes
#
# Requer: NDK 27.1, cmake+ninja+pkgconf+gcc (WinLibs) no PATH, Git Bash. O make e o GNU make 4.3
# (Windows32) que vem no NDK. Fontes em native/src/ffmpeg/dl (baixe com native/ffmpeg/fetch-sources.sh).
set -euo pipefail
export MSYS_NO_PATHCONV=1

HERE="$(cd "$(dirname "$0")" && pwd)"
NATIVE="$(cd "$HERE/.." && pwd)"
m() { cygpath -m "$1" 2>/dev/null || echo "$1"; }
DL="$(m "$NATIVE/src/ffmpeg/dl")"
BUILD="$(m "$NATIVE/build-ffmpeg")"
PREFIX="$BUILD/prefix"
OUT="$(m "$NATIVE/out-ffmpeg/arm64-v8a")"
NDK="$(m "${ANDROID_NDK:-$LOCALAPPDATA/Android/Sdk/ndk/27.1.12297006}")"
API=24
ABI=arm64-v8a
TRIPLE=aarch64-linux-android
JOBS="${JOBS:-$(nproc)}"
TC="$NDK/toolchains/llvm/prebuilt/windows-x86_64"
MAKE="$NDK/prebuilt/windows-x86_64/bin/make.exe"
export CC="$TC/bin/clang.exe"
export AR="$TC/bin/llvm-ar.exe"
export RANLIB="$TC/bin/llvm-ranlib.exe"
export NM="$TC/bin/llvm-nm.exe"
export STRIP="$TC/bin/llvm-strip.exe"
READELF="$TC/bin/llvm-readelf.exe"
TARGET_FLAGS="--target=$TRIPLE$API"
OPT_FLAGS="-O2 -fPIC -DNDEBUG -ffunction-sections -fdata-sections"

FFMPEG_VER=8.1.3
OGG_VER=1.3.6
VORBIS_VER=1.3.7
LAME_VER=3.100
ZLIB_VER=1.3.1

STAGE="${1:-all}"
[ "${CLEAN:-0}" = 1 ] && [ "$STAGE" = all ] && rm -rf "$BUILD"
mkdir -p "$BUILD/src" "$PREFIX/include" "$PREFIX/lib/pkgconfig" "$OUT"

untar() { # $1=arquivo em $DL  $2=pasta de destino em $BUILD/src
  local dst="$BUILD/src/$2"
  if [ ! -d "$dst" ]; then
    rm -rf "$dst.tmp"; mkdir -p "$dst.tmp"
    tar --force-local -xaf "$DL/$1" -C "$dst.tmp" --strip-components=1
    mv "$dst.tmp" "$dst"
  fi
  echo "$dst"
}

cmake_android() { # $1=nome  $2=fonte  resto=flags
  local name="$1" src="$2"; shift 2
  echo "=== $name"
  rm -rf "$BUILD/$name"
  cmake -S "$src" -B "$BUILD/$name" -G Ninja \
    -DCMAKE_TOOLCHAIN_FILE="$NDK/build/cmake/android.toolchain.cmake" \
    -DANDROID_ABI=$ABI -DANDROID_PLATFORM=android-$API \
    -DCMAKE_BUILD_TYPE=Release -DCMAKE_POLICY_VERSION_MINIMUM=3.5 \
    -DCMAKE_POSITION_INDEPENDENT_CODE=ON -DBUILD_SHARED_LIBS=OFF \
    -DCMAKE_C_FLAGS="-ffunction-sections -fdata-sections" \
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
  for o in "${objs[@]}"; do [ -f "$o" ] || { echo "falhou: $o"; exit 1; }; done
  rm -f "$PREFIX/lib/$lib"
  "$AR" rcs "$PREFIX/lib/$lib" "${objs[@]}"
}

build_deps() {
  # zlib (CMake; so o estatico, para nao cair no libz.so do sistema)
  local s; s=$(untar zlib-$ZLIB_VER.tar.gz zlib)
  cmake_android zlib "$s" -DZLIB_BUILD_EXAMPLES=OFF
  rm -f "$PREFIX"/lib/libz.so*
  cp "$PREFIX/share/pkgconfig/zlib.pc" "$PREFIX/lib/pkgconfig/"

  # libogg (CMake)
  s=$(untar libogg-$OGG_VER.tar.xz libogg)
  cmake_android libogg "$s" -DINSTALL_DOCS=OFF -DBUILD_TESTING=OFF

  # libvorbis (CMake; acha o Ogg pelo OggConfig instalado acima)
  s=$(untar libvorbis-$VORBIS_VER.tar.xz libvorbis)
  cmake_android libvorbis "$s" -DOgg_DIR="$PREFIX/lib/cmake/Ogg" -DBUILD_TESTING=OFF
  # o CMake do libvorbis so poe -lm no .pc quando acha libm no host; aqui fica vazio
  sed -i 's/^Libs.private:.*/Libs.private: -lm/' "$PREFIX/lib/pkgconfig/vorbis.pc"

  # LAME (so libmp3lame, sem o decodificador mpglib nem o frontend; o configure autotools nao
  # roda bem aqui, entao compila direto com um config.h fixo para aarch64/bionic)
  s=$(untar lame-$LAME_VER.tar.gz lame)
  echo "=== lame"
  mkdir -p "$BUILD/lame-cfg"
  cat > "$BUILD/lame-cfg/config.h" <<'EOF'
/* config.h fixo para Android aarch64 (bionic, clang) */
#define STDC_HEADERS 1
#define HAVE_ERRNO_H 1
#define HAVE_FCNTL_H 1
#define HAVE_INTTYPES_H 1
#define HAVE_LIMITS_H 1
#define HAVE_STDINT_H 1
#define HAVE_STDLIB_H 1
#define HAVE_STRING_H 1
#define HAVE_STRINGS_H 1
#define HAVE_SYS_STAT_H 1
#define HAVE_SYS_TYPES_H 1
#define HAVE_UNISTD_H 1
#define HAVE_INT8_T 1
#define HAVE_INT16_T 1
#define HAVE_INT32_T 1
#define HAVE_INT64_T 1
#define HAVE_UINT8_T 1
#define HAVE_UINT16_T 1
#define HAVE_UINT32_T 1
#define HAVE_UINT64_T 1
#define HAVE_LONG_DOUBLE 1
#define HAVE_STRTOL 1
#define SIZEOF_SHORT 2
#define SIZEOF_UNSIGNED_SHORT 2
#define SIZEOF_INT 4
#define SIZEOF_UNSIGNED_INT 4
#define SIZEOF_LONG 8
#define SIZEOF_UNSIGNED_LONG 8
#define SIZEOF_LONG_LONG 8
#define SIZEOF_UNSIGNED_LONG_LONG 8
#define SIZEOF_FLOAT 4
#define SIZEOF_DOUBLE 8
#define SIZEOF_LONG_DOUBLE 16
#define TAKEHIRO_IEEE754_HACK 1
#define USE_FAST_LOG 1
#define LAME_LIBRARY_BUILD 1
#define PACKAGE "lame"
#define VERSION "3.100"
typedef double ieee754_float64_t;
typedef float ieee754_float32_t;
EOF
  cc_static libmp3lame.a "$BUILD/lame-obj" \
    "-DHAVE_CONFIG_H -I$BUILD/lame-cfg -I$s/include -I$s/libmp3lame -I$s/mpglib -Wno-shift-negative-value -Wno-absolute-value" \
    "$s"/libmp3lame/{VbrTag,bitstream,encoder,fft,gain_analysis,id3tag,lame,newmdct,presets,psymodel,quantize,quantize_pvt,reservoir,set_get,tables,takehiro,util,vbrquantize,version,mpglib_interface}.c
  mkdir -p "$PREFIX/include/lame"
  cp "$s/include/lame.h" "$PREFIX/include/lame/"

  ls -la "$PREFIX/lib"/*.a
}

# Componentes do FFmpeg (o configure ainda liga sozinho o que o ffmpeg/ffprobe e esses
# componentes exigem, p.ex. os filtros que o fftools/ffmpeg seleciona).
FF_DECODERS="mp3,mp3float,mp2,mp2float,vorbis,flac,aac,aac_fixed,alac,opus,\
pcm_s8,pcm_u8,pcm_s16le,pcm_s16be,pcm_u16le,pcm_u16be,pcm_s24le,pcm_s24be,pcm_u24le,pcm_u24be,\
pcm_s32le,pcm_s32be,pcm_u32le,pcm_u32be,pcm_f32le,pcm_f32be,pcm_f64le,pcm_f64be,pcm_alaw,pcm_mulaw,\
adpcm_ms,adpcm_ima_wav,gsm_ms"
FF_DEMUXERS="mp3,ogg,wav,w64,flac,mov,aiff,aac"
FF_PARSERS="mpegaudio,vorbis,flac,aac,opus"
FF_ENCODERS="pcm_s16le,pcm_u8,libvorbis,libmp3lame,tiff,png"
FF_MUXERS="wav,ogg,mp3,image2"
# showwavespic: forma de onda do editor de som da IDE (Audio.dll, Cache.CreateFFMpegImageArgs -> .tiff)
FF_FILTERS="aresample,aformat,anull,atrim,asetnsamples,showwavespic"

configure_ffmpeg() {
  local s; s=$(untar ffmpeg-$FFMPEG_VER.tar.xz ffmpeg)
  echo "=== ffmpeg: configure"
  mkdir -p "$BUILD/tmp"
  (
    # build dentro da propria arvore (source_path=.): o make Windows32 nao se da bem com VPATH C:/...
    cd "$s"
    [ -f ffbuild/config.mak ] && "$MAKE" distclean > /dev/null 2>&1 || true
    export TMPDIR="$BUILD/tmp"
    export PKG_CONFIG_LIBDIR="$PREFIX/lib/pkgconfig" PKG_CONFIG_PATH=""
    ./configure \
      --prefix="$PREFIX/ffmpeg" \
      --target-os=android --arch=aarch64 --cpu=armv8-a --enable-cross-compile \
      --cc="$CC" --ld="$CC" --ar="$AR" --nm="$NM" --ranlib="$RANLIB" --strip="$STRIP" \
      --host-cc=gcc \
      --pkg-config=pkgconf --pkg-config-flags="--static --dont-define-prefix" \
      --extra-cflags="$TARGET_FLAGS -fPIE -ffunction-sections -fdata-sections -I$PREFIX/include" \
      --extra-ldflags="$TARGET_FLAGS -pie -L$PREFIX/lib -Wl,--gc-sections -Wl,-z,max-page-size=16384 -Wl,--build-id=sha1" \
      --enable-static --disable-shared --enable-pic --enable-pthreads \
      --disable-debug --enable-small --disable-doc --disable-htmlpages --disable-manpages \
      --disable-podpages --disable-txtpages \
      --disable-autodetect --disable-network --disable-everything \
      --enable-ffmpeg --enable-ffprobe --disable-ffplay \
      --disable-avdevice --disable-swscale \
      --enable-avcodec --enable-avformat --enable-avfilter --enable-swresample \
      --enable-libvorbis --enable-libmp3lame --enable-zlib \
      --enable-protocol=file,pipe \
      --enable-decoder="$FF_DECODERS" \
      --enable-demuxer="$FF_DEMUXERS" \
      --enable-parser="$FF_PARSERS" \
      --enable-encoder="$FF_ENCODERS" \
      --enable-muxer="$FF_MUXERS" \
      --enable-filter="$FF_FILTERS"
  ) > "$BUILD/ffmpeg.configure.log" 2>&1 || { tail -40 "$BUILD/ffmpeg.configure.log"; tail -40 "$s/ffbuild/config.log"; exit 1; }
  sed -n '/^Enabled decoders/,$p' "$BUILD/ffmpeg.configure.log" | grep -v "^$" | head -60 || true
}

make_ffmpeg() {
  local s="$BUILD/src/ffmpeg"
  echo "=== ffmpeg: make"
  (cd "$s" && "$MAKE" -j "$JOBS" ffmpeg ffprobe) > "$BUILD/ffmpeg.build.log" 2>&1 \
    || { grep -m20 -B2 -A8 "error" "$BUILD/ffmpeg.build.log" || tail -40 "$BUILD/ffmpeg.build.log"; exit 1; }
  "$STRIP" --strip-all -o "$OUT/libffmpeg.so" "$s/ffmpeg"
  "$STRIP" --strip-all -o "$OUT/libffprobe.so" "$s/ffprobe"
  ls -la "$OUT"
}

# Confere: ET_DYN (PIE), interp /system/bin/linker64, so NEEDED de bibliotecas do sistema.
verify() {
  local ok=1
  for f in "$OUT/libffmpeg.so" "$OUT/libffprobe.so"; do
    echo "--- $(basename "$f") ($(stat -c %s "$f") bytes)"
    local h; h=$("$READELF" -h -l -d "$f" | tr -d '\r')
    echo "$h" | grep -E "Type:|Machine:|interpreter|NEEDED|FLAGS"
    echo "$h" | grep -q "Type:.*DYN" || { echo "ERRO: nao e ET_DYN"; ok=0; }
    echo "$h" | grep -q "interpreter: /system/bin/linker64" || { echo "ERRO: interp"; ok=0; }
    local bad; bad=$(echo "$h" | grep NEEDED | grep -oE '\[[^]]+\]' | tr -d '[]' \
      | grep -vxE 'libc\.so|libm\.so|libdl\.so|liblog\.so' || true)
    [ -z "$bad" ] || { echo "ERRO: NEEDED fora do sistema: $bad"; ok=0; }
  done
  [ $ok = 1 ] && echo "verificacao ok" || exit 1
}

case "$STAGE" in
  deps) build_deps ;;
  ffmpeg) configure_ffmpeg; make_ffmpeg ;;
  ff-build) make_ffmpeg ;;
  verify) verify ;;
  all) build_deps; configure_ffmpeg; make_ffmpeg; verify ;;
  *) echo "estagio desconhecido: $STAGE"; exit 1 ;;
esac
