# ffmpeg / ffprobe para Android arm64: fontes e resultado

Saída (executáveis PIE com nome de lib, para o Android extrair do APK na `nativeLibraryDir`):

- `native/out-ffmpeg/arm64-v8a/libffmpeg.so`  → `ffmpeg`
- `native/out-ffmpeg/arm64-v8a/libffprobe.so` → `ffprobe`

Substituem os `ffmpeg.exe`/`ffprobe.exe` que o GMAssetCompiler chama para converter áudio e que o
editor de som da IDE (`Audio.dll`, `YoYoStudio.Plugins.Audio.Cache`) usa para o cache de reprodução e a
imagem da forma de onda. Só dependem de `libc.so` e `libm.so` do sistema (zlib, libogg, libvorbis e LAME
entram estáticos);
`ET_DYN` + `PIE`, interp `/system/bin/linker64`, páginas de 16 KB, API mínima 24.

Para refazer (Git Bash):

```bash
bash native/ffmpeg/fetch-sources.sh          # baixa e confere o SHA-256 dos tarballs
CLEAN=1 bash native/ffmpeg/build-ffmpeg.sh   # deps + FFmpeg + verificação (NEEDED/ELF)
```

Estágios avulsos: `deps`, `ffmpeg` (configure + make), `ff-build` (só make), `verify`.
Intermediários e logs em `native/build-ffmpeg/` (`libogg.*.log`, `libvorbis.*.log`,
`ffmpeg.configure.log`, `ffmpeg.build.log`; o `config.log` do FFmpeg fica em
`native/build-ffmpeg/src/ffmpeg/ffbuild/`). O configure do FFmpeg leva ~4 min no Git Bash; o make, ~1 min.

Ferramentas: NDK 27.1.12297006 (clang/lld `windows-x86_64`, e o GNU make 4.3 Windows32 de
`ndk/.../prebuilt/windows-x86_64/bin/make.exe`, usado porque o Git Bash não traz make), cmake + ninja +
pkgconf + gcc do WinLibs (o gcc só compila as ferramentas de host do FFmpeg). O FFmpeg é compilado
dentro da própria árvore extraída (`source_path=.`), porque o make Windows32 não lida bem com VPATH `C:/...`.

## Fontes

| Componente | Versão | URL | SHA-256 do tarball | Licença |
|---|---|---|---|---|
| FFmpeg | 8.1.3 | https://ffmpeg.org/releases/ffmpeg-8.1.3.tar.xz | 7138d28c96d9d3e3af4ee3d8cad72741f8ffb40da90c1112235dea3ecd3178a3 | LGPL 2.1+ (configurado sem `--enable-gpl`/`--enable-nonfree`; o configure reporta "License: LGPL version 2.1 or later") |
| libogg | 1.3.6 | https://downloads.xiph.org/releases/ogg/libogg-1.3.6.tar.xz | 5c8253428e181840cd20d41f3ca16557a9cc04bad4a3d04cce84808677fa1061 | BSD-3-Clause |
| libvorbis | 1.3.7 | https://downloads.xiph.org/releases/vorbis/libvorbis-1.3.7.tar.xz | b33cc4934322bcbf6efcbacf49e3ca01aadbea4114ec9589d1b1e9d20f72954b | BSD-3-Clause |
| zlib | 1.3.1 | https://github.com/madler/zlib/releases/download/v1.3.1/zlib-1.3.1.tar.gz | 9a93b2b7dfdac77ceba5a558a580e74667dd6fede4585b91eefb60f03b72df23 | zlib |
| LAME (libmp3lame) | 3.100 | https://downloads.sourceforge.net/project/lame/lame/3.100/lame-3.100.tar.gz | ddfe36cab873794038ae2c1210557ad34857a4b6bdc515785d1da9e175b1da1e | LGPL 2.0+ |

O tarball do FFmpeg foi conferido também com a assinatura `ffmpeg-8.1.3.tar.xz.asc`
(chave de release do FFmpeg, impressão digital `FCF9 86EA 15E6 E293 A564 4F10 B432 2F04 D676 58D8`).

Nota de licença: FFmpeg e LAME são LGPL e estão ligados estaticamente dentro dos executáveis
`ffmpeg`/`ffprobe`. O app só os executa como processos separados. Para cumprir a LGPL ao distribuir o APK,
basta oferecer o código-fonte correspondente (os tarballs acima) e estes scripts, que permitem
recompilar e substituir os executáveis. Inclua os avisos de copyright/licença de FFmpeg, LAME, zlib, libogg e libvorbis
na tela de licenças do app.

## Como cada parte é compilada

- **zlib**: CMake + toolchain Android, só o `libz.a` (o `libz.so` instalado é apagado para o link não cair
  no `libz.so` do sistema). Serve ao encoder `png` e à compressão deflate do `tiff`.
- **libogg**, **libvorbis**: CMake + toolchain Android do NDK, estáticos, `android-24`. O `vorbis.pc` gerado
  em host Windows sai com `Libs.private:` vazio; o script põe `-lm`.
- **LAME**: só `libmp3lame/*.c` (sem `mpglib`, sem frontend, sem `vector/` SSE), compilado direto com um
  `config.h` fixo para aarch64/bionic (escrito pelo script), porque o configure autotools não roda bem aqui.
- **FFmpeg**: `--disable-everything` e só o necessário:

```
--target-os=android --arch=aarch64 --cpu=armv8-a --enable-cross-compile
--cc/--ld=clang.exe --target=aarch64-linux-android24  (+ -fPIE, -pie, -Wl,-z,max-page-size=16384, -Wl,--gc-sections)
--enable-static --disable-shared --enable-pic --enable-pthreads
--disable-debug --enable-small --disable-doc
--disable-autodetect --disable-network --disable-everything
--enable-ffmpeg --enable-ffprobe --disable-ffplay --disable-avdevice --disable-swscale
--enable-avcodec --enable-avformat --enable-avfilter --enable-swresample
--enable-libvorbis --enable-libmp3lame --enable-zlib
--enable-protocol=file,pipe
--enable-decoder=mp3,mp3float,mp2,mp2float,vorbis,flac,aac,aac_fixed,alac,opus,
                 pcm_{s8,u8,s16le,s16be,u16le,u16be,s24le,s24be,u24le,u24be,s32le,s32be,u32le,u32be,
                      f32le,f32be,f64le,f64be,alaw,mulaw},adpcm_ms,adpcm_ima_wav,gsm_ms
--enable-demuxer=mp3,ogg,wav,w64,flac,mov,aiff,aac
--enable-parser=mpegaudio,vorbis,flac,aac,opus
--enable-encoder=pcm_s16le,pcm_u8,libvorbis,libmp3lame,tiff,png
--enable-muxer=wav,ogg,mp3,image2
--enable-filter=aresample,aformat,anull,atrim,asetnsamples,showwavespic
```

O configure ainda liga sozinho os filtros que o `fftools/ffmpeg` exige (`crop`, `format`, `hflip`, `null`,
`rotate`, `transpose`, `trim`, `vflip`). Assembly NEON do aarch64 fica ligado.

## Comandos que o GMAssetCompiler usa (todos testados num build x86_64 com a mesma seleção de componentes)

Com `-hide_banner -nostats -loglevel 0` antes:

```
-y -i "in" -vn -filter_complex "aresample=ochl=1c" -ar 44100 -acodec pcm_s16le -bitexact "out.wav"
-y -i "in" -acodec libvorbis -bitexact -filter_complex "aresample=ochl=2c" -ar 44100 -aq 4 -loglevel quiet -vn "out.ogg"
-y -i "in" -vn -acodec libmp3lame -bitexact -filter_complex "aresample=ochl=2c" -ar 44100 -aq 4 "out.mp3"
```

e `ffprobe -hide_banner -i <arquivo>`, que imprime (em stderr) `  Duration: HH:MM:SS.xx, ...`.

## Comandos do editor de som da IDE (`Audio.dll`, `YoYoStudio.Plugins.Audio.Cache`, decompilado com ilspycmd)

`CreateFFMpegAudioArgs` (cache para tocar; mesmos codecs do compilador, taxa = a do asset):

```
-loglevel quiet -y -i "src" -bitexact -vn -ar <sampleRate> -acodec pcm_u8|pcm_s16le -filter_complex "aresample=ochl=1c|2c" "dst.wav"
-loglevel quiet -y -i "src" -bitexact -vn -ar <sampleRate> -acodec libvorbis -aq <quality> -filter_complex "aresample=ochl=1c|2c" "dst.ogg"
```

`CreateFFMpegImageArgs` (forma de onda; `showwavespic` gera RGBA, que o encoder `tiff` aceita direto,
então não precisa de swscale):

```
-y -i "src" -filter_complex "aresample=ochl=mono,showwavespic=s=450x150:colors=#ffffff:scale=sqrt" "dst.tiff"
```

Testados no build x86_64 com a mesma seleção: a forma de onda sai como TIFF 450x150 (PackBits) e também
como `.png` (RGBA); o muxer image2 avisa em stderr que quer `-update` para imagem única, mas grava e
sai com código 0. O cache de áudio foi testado com `-ar 22050`/`48000`, `pcm_u8` e `libvorbis -aq 7`.
