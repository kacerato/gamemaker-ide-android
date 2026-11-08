# native/extras — libs nativas Android arm64 extras da IDE

Saída: `native/out-extras/arm64-v8a/`. Para refazer tudo (download, build, verificação):

    bash native/extras/build-extras.sh

O script baixa o HarfBuzzSharp, confere a assinatura do pacote, compila os stubs (NDK 27.1, API 26,
`-z max-page-size=16384`) e confere com `llvm-nm -D` que cada .so exporta **todos** os entry points
que o assembly consumidor declara (lista feita com `tools/list-pinvokes.ps1`, System.Reflection.Metadata).

## Bibliotecas

| Arquivo (.so) | DllImport pedido pelo assembly | Assembly consumidor | Origem | Imports verificados |
|---|---|---|---|---|
| `libHarfBuzzSharp.so` | `libHarfBuzzSharp` | `HarfBuzzSharp.dll` 8.3.1.1 | NuGet oficial | 319/319 |
| `libIDENativeLib.so` | `IDENativeLib` | `DebugInfo.dll` | stub C (`src/IDENativeLib.c`) | 1/1 |
| `libtinyfiledialogs.so` | `tinyfiledialogs` (Linux dllmap em `OSCore.dll.config`: `libTinyFileDialogs.so`) | `OSCore.dll` | reimplementação C (`src/tinyfiledialogs.c`) | 18/18 |

`host/NativeResolver.cs` já mapeia `tinyfiledialogs` e `libTinyFileDialogs.so` para `libtinyfiledialogs.so`.
`libHarfBuzzSharp` e `IDENativeLib` são resolvidos pelo Mono com o prefixo `lib` + `.so`, mas um mapeamento explícito não atrapalha.

### HarfBuzzSharp
- Pacote: `HarfBuzzSharp.NativeAssets.Android` **8.3.1.1**
  <https://www.nuget.org/api/v2/package/HarfBuzzSharp.NativeAssets.Android/8.3.1.1>
- nupkg sha256 `68fb413b165937ea63f7990551dfc1c2cdcddc5741b427b1bf10a42a0dfdc3f4`; `dotnet nuget verify --all` OK
  (assinatura de autor Microsoft Corporation + repositório nuget.org).
- Mesmo commit do `HarfBuzzSharp.dll` da IDE (ProductVersion `8.3.1.1+release-3.119.0.64f24b1c…`).
- Arquivo: `runtimes/android-arm64/native/libHarfBuzzSharp.so`, sha256 `d227df16…16e2`, NEEDED só libc/libm/libdl,
  segmentos LOAD alinhados em 16 KB.
- O `HarfBuzzSharp.dll` também importa `libc!gnu_get_libc_version` (não existe na bionic), mas só via
  `PlatformConfiguration.IsGlibc`, que só roda quando `OperatingSystem.IsLinux()` é true (é false no Android).

### IDENativeLib (stub)
- No Windows, `IDENativeLib.dll` exporta só `DieDieDie` (ordinal 1): `mov dword ptr [0], 1234; ret`, um
  crash proposital. Declaração gerenciada: `static extern void DieDieDie()` (sem parâmetros, retorno void),
  chamada só por `DebugInfoModule.DebugInfo_Die` (comando de debug para testar o caminho de crash).
- O stub faz o mesmo: grava no logcat (tag `IDENativeLib`), escreve em NULL (SIGSEGV) e chama `abort()` se sobreviver.

### tinyfiledialogs (reimplementação)
- `OSCore.dll` (`Core.CoreOS.System.tinyfd`) usa a API tinyfiledialogs 3.x no Linux; o original chama
  zenity/kdialog, que não existem no Android, e nenhum `libTinyFileDialogs.so` vem na instalação Windows.
- `tinyfd_messageBox[W]`: abre um `SDL_ShowMessageBox` (procurado em `libSDL2.so` com dlopen/dlsym, sem dependência
  de link). Valor de retorno igual ao tinyfd (ok/yes=1, no=2 em yesnocancel, cancel/no=0). Sem SDL: 1 para "ok", 0 para os outros.
- `tinyfd_notifyPopup[W]`: logcat (tag `tinyfd`), retorna 1. `tinyfd_beep`: no-op.
- Diálogos de abrir/salvar/pasta/cor/input: retornam NULL (= cancelado). Os diálogos de arquivo reais ficam em
  `host/AndroidFileDialogs.cs` (SAF).
- `tinyfd_getGlobalChar/GetGlobalInt/SetGlobalInt`: nomes documentados (`tinyfd_version`, `tinyfd_response`,
  `tinyfd_verbose`, `tinyfd_silent`, …); nome desconhecido → NULL / -1.
- As variantes `W` têm a mesma assinatura gerenciada das estreitas (string → UTF-8) e só repassam a chamada.

## Varredura de ModuleRefs (o que ficou de fora e por quê)
`pinvokes-all.tsv` = todos os P/Invokes de todos os .dll da instalação (Assembly, módulo, entry point, método).

| Módulo | Quem usa | Decisão |
|---|---|---|
| SDL2, FNA3D, freetype.dll, onig.dll, yoga.dll, yyal.dll, Magick.Native-* | vários | já providenciados em `native/out` / outra sessão |
| SDL2_image, SDL2_ttf, SDL2_mixer, SDL2_gfx | só `SDL2-CS.dll` | nenhum outro assembly referencia `SDL2.SDL_image/ttf/mixer/gfx` (MemberRefs conferidos): não são chamados |
| steam_api64, sdkencryptedappticket64 | `Steamworks.NET.dll` (IDE.dll chama `SteamAPI.Init/RunCallbacks/Shutdown`, `SteamUser.*`) | não vem nem na instalação Windows; a IDE já roda sem ela. Não existe build Android do Steamworks. Sem stub |
| libc (`getppid`, `exit`, `uname`, `prctl`, `sysctlbyname`, `gnu_get_libc_version`) | GMLS, IDE, OSCore, Utils, HarfBuzzSharp | bionic (`libc.so`, mapeado no NativeResolver). `sysctlbyname` é só macOS e `gnu_get_libc_version` só glibc; os dois ficam em caminhos que não rodam no Android |
| libdl.dylib, /usr/lib/libobjc.dylib, frameworks /System/Library/…, ./Mac/objcWrapper | OSCore e outros | só macOS |
| System.Globalization.Native, System.IO.Compression.Native, hostpolicy, QCall | framework | fornecidos pelo runtime Mono do Android (ou internos) |
| msquic | `System.Net.Quic` (carregado com `NativeLibrary.TryLoad`) | opcional; sem ela o QUIC fica indisponível, sem erro |
| PresentationNative_cor3, wpfgfx_cor3, PenImc_cor3, kernel32, user32, gdi32 etc. | WPF/WinForms/Windows | APIs Windows, ignoradas |

Carregamentos dinâmicos (`NativeLibrary.Load/GetExport`): `Utils.dll` (resolver de `yoga.dll` via .dll.config),
`CodeEditor2.dll` (`onig.dll`), `IDE.dll` (só checa se existe `steam_api64.dll`/`libsteam_api.so`), `GameMakerLanguageServer.dll`
(nenhum nome de lib nativa nas strings). Nenhum pede outra lib.

## Arquivos
- `build-extras.sh` — download + build + verificação
- `src/IDENativeLib.c`, `src/tinyfiledialogs.c` — fontes dos stubs
- `tools/list-pinvokes.ps1` — lista todos os P/Invokes de uma pasta de assemblies
- `tools/list-typerefs.ps1` — lista que assemblies referenciam membros de um tipo (achar quem chama)
- `pinvokes-all.tsv` — saída da varredura completa
- `.work/` — downloads e arquivos temporários do script (pode apagar)
