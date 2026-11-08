#!/usr/bin/env bash
# Baixa os tarballs usados por build-ffmpeg.sh para native/src/ffmpeg/dl e confere o SHA-256
# (ver SOURCES.md). Uso (Git Bash): bash native/ffmpeg/fetch-sources.sh
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
DL="$HERE/../src/ffmpeg/dl"
mkdir -p "$DL"
cd "$DL"

dl() { # $1=arquivo  $2=sha256  $3=url
  if [ ! -f "$1" ]; then
    curl -sSfL --retry 3 -o "$1.part" "$3"
    mv "$1.part" "$1"
  fi
  local got; got=$(sha256sum "$1" | cut -d' ' -f1)
  if [ "$got" != "$2" ]; then
    echo "SHA-256 errado para $1: $got (esperado $2)" >&2
    rm -f "$1"; exit 1
  fi
  echo "$1 ok"
}

# FFmpeg 8.1.3 (assinatura .asc conferida com a chave FCF986EA15E6E293A5644F10B4322F04D67658D8)
dl ffmpeg-8.1.3.tar.xz    7138d28c96d9d3e3af4ee3d8cad72741f8ffb40da90c1112235dea3ecd3178a3 \
  https://ffmpeg.org/releases/ffmpeg-8.1.3.tar.xz
dl libogg-1.3.6.tar.xz    5c8253428e181840cd20d41f3ca16557a9cc04bad4a3d04cce84808677fa1061 \
  https://downloads.xiph.org/releases/ogg/libogg-1.3.6.tar.xz
dl libvorbis-1.3.7.tar.xz b33cc4934322bcbf6efcbacf49e3ca01aadbea4114ec9589d1b1e9d20f72954b \
  https://downloads.xiph.org/releases/vorbis/libvorbis-1.3.7.tar.xz
dl lame-3.100.tar.gz      ddfe36cab873794038ae2c1210557ad34857a4b6bdc515785d1da9e175b1da1e \
  https://downloads.sourceforge.net/project/lame/lame/3.100/lame-3.100.tar.gz
# zlib (mesmo tarball do native/magick): encoder png e compressao deflate do tiff
dl zlib-1.3.1.tar.gz      9a93b2b7dfdac77ceba5a558a580e74667dd6fede4585b91eefb60f03b72df23 \
  https://github.com/madler/zlib/releases/download/v1.3.1/zlib-1.3.1.tar.gz
