#!/usr/bin/env bash
# Baixa os fontes usados por build-magick.sh para native/src/magick (ver SOURCES.md).
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
SRC="$HERE/../src/magick"
mkdir -p "$SRC/dl"
cd "$SRC"

[ -d ImageMagick ] || git -c advice.detachedHead=false clone -q --depth 1 --branch 7.1.2-8 \
  https://github.com/ImageMagick/ImageMagick.git ImageMagick
# 7.1.2-8 == a3b13d143fd7dea44cd71d31aa02f411b597688f (src/ImageMagick/ImageMagick.commit do Magick.Native)

dl() { [ -f "dl/$1" ] || curl -sSfL -o "dl/$1" "$2"; echo "$1 ok"; }
dl zlib-1.3.1.tar.gz          https://github.com/madler/zlib/releases/download/v1.3.1/zlib-1.3.1.tar.gz
dl bzip2-1.0.8.tar.gz         https://sourceware.org/pub/bzip2/bzip2-1.0.8.tar.gz
dl xz-5.8.1.tar.gz            https://github.com/tukaani-project/xz/releases/download/v5.8.1/xz-5.8.1.tar.gz
dl libpng-1.6.50.tar.gz       https://github.com/pnggroup/libpng/archive/refs/tags/v1.6.50.tar.gz
dl libjpeg-turbo-3.1.2.tar.gz https://github.com/libjpeg-turbo/libjpeg-turbo/releases/download/3.1.2/libjpeg-turbo-3.1.2.tar.gz
dl freetype-2.14.1.tar.gz     https://github.com/freetype/freetype/archive/refs/tags/VER-2-14-1.tar.gz
dl lcms2-2.17.tar.gz          https://github.com/mm2/Little-CMS/releases/download/lcms2.17/lcms2-2.17.tar.gz
dl libxml2-2.15.1.tar.gz      https://github.com/GNOME/libxml2/archive/refs/tags/v2.15.1.tar.gz
dl libwebp-1.6.0.tar.gz       https://github.com/webmproject/libwebp/archive/refs/tags/v1.6.0.tar.gz
dl tiff-4.7.1.tar.gz          https://download.osgeo.org/libtiff/tiff-4.7.1.tar.gz
