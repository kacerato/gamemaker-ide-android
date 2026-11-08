# Magick.Native para Android arm64: fontes e resultado

Saída: `native/out-magick/arm64-v8a/libMagick.Native-Q8-arm64.so`
(substitui o `Magick.Native-Q8-arm64.dll` que o Magick.NET-Q8-AnyCPU 14.9.1 da IDE procura).

Para refazer o build (Git Bash):

```bash
bash native/magick/fetch-sources.sh   # só se faltar algum fonte
bash native/magick/build-magick.sh    # deps + ImageMagick + Magick.Native + verificação de ABI
```

Os intermediários ficam em `native/build-magick/` (logs: `deps.log`, `*.configure.log`, `*.build.log`,
`im.configure.log`, `im.build.log`, `native.build.log`, `link.log`).

## Fontes

| Componente | Versão | URL | SHA-256 do tarball |
|---|---|---|---|
| Magick.NET | 14.9.1 (tag) | https://github.com/dlemstra/Magick.NET | (git) |
| Magick.Native | commit `381705918d2c74a1a9b46702259c8cfc3f4de344` | https://github.com/dlemstra/Magick.Native | (git) |
| ImageMagick | 7.1.2-8 (tag, = commit `a3b13d143fd7dea44cd71d31aa02f411b597688f`, o mesmo de `src/ImageMagick/ImageMagick.commit` do Magick.Native) | https://github.com/ImageMagick/ImageMagick | (git) |
| zlib | 1.3.1 | https://github.com/madler/zlib/releases/download/v1.3.1/zlib-1.3.1.tar.gz | 9a93b2b7dfdac77ceba5a558a580e74667dd6fede4585b91eefb60f03b72df23 |
| bzip2 | 1.0.8 | https://sourceware.org/pub/bzip2/bzip2-1.0.8.tar.gz | ab5a03176ee106d3f0fa90e381da478ddae405918153cca248e682cd0c4a2269 |
| xz (liblzma) | 5.8.1 | https://github.com/tukaani-project/xz/releases/download/v5.8.1/xz-5.8.1.tar.gz | 507825b599356c10dca1cd720c9d0d0c9d5400b9de300af00e4d1ea150795543 |
| libpng | 1.6.50 | https://github.com/pnggroup/libpng/archive/refs/tags/v1.6.50.tar.gz | 71158e53cfdf2877bc99bcab33641d78df3f48e6e0daad030afe9cb8c031aa46 |
| libjpeg-turbo | 3.1.2 | https://github.com/libjpeg-turbo/libjpeg-turbo/releases/download/3.1.2/libjpeg-turbo-3.1.2.tar.gz | 8f0012234b464ce50890c490f18194f913a7b1f4e6a03d6644179fa0f867d0cf |
| freetype | 2.14.1 | https://github.com/freetype/freetype/archive/refs/tags/VER-2-14-1.tar.gz | 44bd69d1f0750603410cfd4f26f1c5523c5a3a087a2dfa8639b757dc7c6677be |
| lcms2 | 2.17 | https://github.com/mm2/Little-CMS/releases/download/lcms2.17/lcms2-2.17.tar.gz | d11af569e42a1baa1650d20ad61d12e41af4fead4aa7964a01f93b08b53ab074 |
| libxml2 | 2.15.1 | https://github.com/GNOME/libxml2/archive/refs/tags/v2.15.1.tar.gz | 23b1ebd74cc562cd592cd2618c2bd88dc06fa0dfdbcd56e03cb26d6ff6b7e373 |
| libwebp | 1.6.0 | https://github.com/webmproject/libwebp/archive/refs/tags/v1.6.0.tar.gz | 93a852c2b3efafee3723efd4636de855b46f9fe1efddd607e1f42f60fc8f2136 |
| libtiff | 4.7.1 | https://download.osgeo.org/libtiff/tiff-4.7.1.tar.gz | f698d94f3103da8ca7438d84e0344e453fe0ba3b7486e04c5bf7a9a3fabe9b69 |

## Configuração do ImageMagick

As mesmas opções de `build/shared/build.imagemagick.sh` do Magick.Native (Linux): estático, `--with-quantum-depth=8`,
`--enable-hdri=no`, `--disable-openmp`, `--enable-64bit-channel-masks` (`MAGICKCORE_CHANNEL_MASK_DEPTH=64`, obrigatório
para o `ChannelType` de 64 bits que o Magick.NET usa), `--disable-opencl`, `--disable-deprecated`, sem Magick++/utilitários.
Diferença: o oficial usa `--enable-force-cpp` (compila o ImageMagick como C++ por causa do `enum : MagickOffsetType`).
O clang aceita esse enum em C como extensão, então o ImageMagick é compilado em C (o layout é o mesmo); os `.c` do
Magick.Native são compilados como C++, igual ao CMakeLists oficial.

O `configure` roda só para gerar `config.h`/`magick-baseconfig.h`. A compilação usa um `build.ninja` gerado pelo script a
partir das listas de fontes dos `Makefile.am` (libtool/make do autotools não funcionam bem no Git Bash).

## Delegates

Incluídos (todos estáticos, `-fPIC`): zlib, bzip2, lzma (xz), libpng, libjpeg-turbo, libtiff (zlib/jpeg/lzma/pixarlog/old-jpeg),
libwebp (+mux/demux/sharpyuv), freetype (só zlib), lcms2, libxml2 (sem iconv/icu/python/http).
Coders internos (sem delegate) também entram: GIF, BMP, PSD, TGA, ICO, DDS, QOI, PNM, SVG interno (MSVG), MVG, TXT, etc.

Deixados de fora (o oficial tem; precisariam de mais trabalho de cross-compilação):

| Delegate | Formatos afetados | Motivo |
|---|---|---|
| librsvg + cairo + pango + glib + harfbuzz + fribidi + raqm + fontconfig | SVG via rsvg (fica o MSVG interno), PANGO (texto rico) | cadeia glib/meson grande |
| libheif + aom + libde265 + openh264 | HEIC/AVIF | grande, precisa de nasm/meson |
| libjxl + brotli + highway | JPEG XL | C++ grande |
| libraw | DNG/RAW de câmera | pouco útil para a IDE |
| OpenEXR + Imath | EXR | C++ grande |
| openjpeg | JPEG 2000 | dá para adicionar fácil (CMake) se precisar |
| liblqr, libdeflate, libzip, fftw, ghostscript, djvu, jbig, wmf, uhdr, zstd | liquid rescale, ORA via zip, PDF/PS (ghostscript nunca vai junto) etc. | fora do mínimo |

## Verificação de ABI

- Esperado: os 693 P/Invokes de `Magick.NET-Q8-AnyCPU.dll` 14.9.1 com escopo `Magick.Native-Q8-arm64.dll`
  (extraídos do ImplMap do assembly; lista em `native/magick/abi-expected.txt`).
- Exportado (`llvm-nm -D --defined-only`): 698 símbolos, **0 faltando**. Os 5 extras (`MagickColor_Clone`,
  `MagickImageCollection_Fx`, `MagickImage_SetClientData`, `MagickImage_WriteBlob`, `TypeMetric_Create`) existem no código
  do Magick.Native mas o Magick.NET 14.9.1 não chama.
- A lista exportada é idêntica à de `C:\Program Files\GameMaker-LTS2026\Magick.Native-Q8-x64.dll` (698 = 698).
- Só exporta os símbolos `MAGICK_NATIVE_EXPORT` (version script); MagickCore/delegates ficam internos.
- Dependências dinâmicas: só `libc.so`, `libm.so`, `libdl.so`, `liblog.so` (libc++ estática). Alinhamento de LOAD 16 KB.
  Símbolos de libc mais novos usados: `strtod_l@LIBC_O` (API 26) e `__write_chk@LIBC_N` (API 24), compatível com minSdk 26.

## Arquivos XML de configuração (coder.xml, policy.xml, ...)

O Magick.NET não precisa deles em disco. No Windows o Magick.Native embute os XML como recursos (`Magick_GetWindowsResource`);
no Linux/macOS (e aqui) não há nada embutido e o ImageMagick usa os padrões compilados (cores, coders, política
aberta, etc.). `Environment_Initialize` chama `MagickCoreGenesis(NULL, MagickFalse)`.
O ImageMagick procura XML em `$MAGICK_CONFIGURE_PATH`, `/usr/local/etc/ImageMagick-7/`, `/usr/local/share/ImageMagick-7/`,
`~/.config/ImageMagick` e na pasta do executável. Nenhum desses existe no Android, que é o mesmo comportamento do
pacote Linux oficial. Se um dia for preciso uma `policy.xml` (limites de recursos), dá para chamar
`MagickNET.Initialize(path)` ou definir `MAGICK_CONFIGURE_PATH` antes da primeira chamada.

## Nome do arquivo e carregamento pelo .NET

O `[DllImport]` usa o nome `Magick.Native-Q8-arm64.dll`. No Linux o .NET tenta, entre outros, `libMagick.Native-Q8-arm64.dll.so`
e `Magick.Native-Q8-arm64.dll.so` (o pacote oficial Linux chama o arquivo `Magick.Native-Q8-arm64.dll.so`), **não**
`libMagick.Native-Q8-arm64.so`. O host precisa de um destes:
- um `NativeLibrary.SetDllImportResolver` no assembly `Magick.NET-Q8-AnyCPU` que mapeie `Magick.Native-Q8-arm64.dll` para
  `libMagick.Native-Q8-arm64.so`, ou
- empacotar o arquivo também (ou só) como `libMagick.Native-Q8-arm64.dll.so` (o nome começa com `lib` e termina em `.so`,
  então o instalador do Android extrai normalmente).
O SONAME interno é `libMagick.Native-Q8-arm64.so`; nenhuma outra lib depende dele pelo nome, então renomear o arquivo é seguro.
