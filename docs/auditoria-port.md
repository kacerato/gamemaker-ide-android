# Auditoria de fidelidade do port Android (IDE GameMaker LTS2026)

Data: 07/11/2026. Só investigação. Nenhum arquivo do projeto foi alterado além deste e nenhum build foi feito.

O objetivo é listar o que ainda não funciona, ou funciona diferente, antes de criar um jogo do zero no aparelho.
Cada item traz o membro decompilado que confirma a causa. O que não deu para confirmar está marcado como
**não verificado** ou **provável**.

## 0. Método e artefatos

| Artefato | SHA-256 (início) |
|---|---|
| `IDE.dll` 2026.0.0.16 | `e80b812544c0` (MVID `dca738c6-…`) |
| `Build.dll` | `9e47dc05b6c2` |
| `Utils.dll` 2026.0.0.16 | `3aebc3ce952e` (MVID `7c0b8672-…`) |
| `Audio.dll` | `25352c87dc4e` (MVID `77e1154b-…`) |
| `OSCore.dll` | `66b870429623` |
| `Graphics.dll` | `bcf29841f4c4` |
| `SoundEditor.dll` / `AssetBrowser.dll` / `HelpPlugins.dll` | `fb51ba2b11fa` / `801a4b9b96b4` / `0062d5339846` |
| `GmpmBindings.dll` / `SourceControlGit.dll` / `YYZImportExport.dll` | `dc855f6e804c` / `fac26ce4305a` / `32484bf6d7cc` |
| `ImageEditor.dll` / `FontEditor.dll` / `ExternalEditors.dll` | `fddd0b9048f5` / `2932f8d0e577` / `8e9e89eb79fa` |
| `System.Diagnostics.Process.dll` (pack `Microsoft.NETCore.App.Runtime.Mono.android-arm64` 8.0.29) | usado para confirmar o comportamento de `Process.Start` no Android |

- **ilspycmd 8.2:** decompilei os 103 assemblies da YoYo que têm `.gmplugin`, mais `GameMaker-LTS2026`,
  `CommonResources`, `PluginWarningHandler` e `DependencyResolver`. Depois procurei por `Process.Start`,
  `ProcessStartInfo`, `CmdProcess`, `Platform.OSType`, `Registry`, clipboard, fontes e URLs.
- **P/Invoke:** usei a varredura completa já existente (`native/extras/pinvokes-all.tsv`, 8.771 linhas, feita com
  `tools/list-pinvokes.ps1`). Filtrei os assemblies da YoYo e conferi cada módulo Windows na decompilação.
- **REA**, `inspect_managed_members`:
  - `Audio.dll`, tipo `YoYoStudio.Plugins.Audio.Cache`: `ev_b4a5f659…ee3ca9`. Página dos literais:
    `ev_384f8641…b5a4`, com `"/usr/bin/"`, `"/ffmpeg"` e `showwavespic=s=450x150`.
  - `Utils.dll` `SystemFonts`: `ev_fa7d009a…9029`.
  - `IDE.dll` `YoYoStudio.IDE`: `ev_211d9db6…117e`.
- **Aparelho** (POCO 25053PC47G, só leitura, sem tocar na tela):
  - `Download/gmide-log.txt` (processo 17522, 22:33–22:51) e `gmide-log.prev.txt` copiados por `adb pull`.
  - `adb shell command -v` para as ferramentas externas. Saiu "ausente" para `xdg-open`, `gnome-open`,
    `kfmclient`, `git`, `ffmpeg`, `lsb_release`, `glxinfo` e `bash`, e `/usr/bin` não existe.
  - O buffer atual do logcat não tinha linhas `GmIde*` (o app não estava rodando).
  - Nos dois logs não aparece nenhuma linha `GmIde.Native: NAO resolvido`. Só foram carregados `Magick`, `freetype`,
    `onig`, `yyal` e `libc`. Os avisos `monodroid-assembly: Shared library 'Magick.Native-Q8-arm64.dll' not loaded`
    são do override do monodroid, e o `NativeResolver` resolve logo em seguida.
- Já resolvido e só citado aqui: `docs/run-preview-analise.md`, `docs/apk-export.md`, `docs/login-entrypoints.md`,
  as notas da pasta compartilhada (template-hang-fix, unsafe-location, ffmpeg-android) e os patches em `host/`.

### Comportamento de `Process.Start` no Android (base de vários itens)

No `System.Diagnostics.Process.dll` do pack android-arm64:

- `Process.GetPathToOpenFile()` só procura `xdg-open`, `gnome-open` e `kfmclient` no PATH.
- Quando não encontra nenhum, `ForkAndExecProcess(... resolvedFilename: null ...)` lança `Win32Exception` (ENOENT).
- Com `UseShellExecute=false`, executável inexistente também dá `Win32Exception`.

Como `Platform.OSType == 2` (Linux) no app, todos os ramos "Linux" da IDE que chamam programas do sistema falham assim.

### Tratamento de exceção na IDE

- Quem trata o erro (try/catch) muda de lugar para lugar. `CmdProcess.StartProcess` (Utils) captura a exceção e
  devolve -1, e `IDE.OpenBrowser` também captura. `FileExplorer_Linux.OpenLocation` (OSCore) não captura.
- `SystemWeakEvent.Throw` (Utils), que dispara cliques e menus, só tem `try/finally`, e o laço `IDE.Run()` não tem
  `try/catch`.
- Uma exceção que escapa de um handler chega ao `catch` de `IDE.Execute` → `FatalErrorWarning` +
  `CrashReporter.WriteStackDumpFile`, e o `Execute` termina.
- **Provável efeito:** a IDE fecha. Não reproduzi no aparelho.

---

## 1. Resumo: o que mais vai pesar ao criar um jogo

1. **Rodar o jogo (F5/F6) não funciona.** Em implementação noutra sessão, com causa em `docs/run-preview-analise.md`:
   ícone Linux ausente → `WinTrust.VerifySecurity` sem patch → `Test = Linux`. Até lá, o caminho para testar é
   **Gerar APK** e instalar.
2. **Editor de som sem pré-escuta e sem forma de onda.** `Audio.dll` `Cache.Init()` aponta o ffmpeg para
   `/usr/bin/ffmpeg` no Linux. O patch de ffmpeg do port só cobre o `GMAssetCompiler` (`IgorPatches.PatchAudioTools`).
   O som no **jogo compilado** é outro caminho (já corrigido, não testado no aparelho).
3. **Font Editor com poucas fontes.**
   - `SystemFonts.GetLinuxSystemFonts()` devolve lista vazia.
   - A IDE só varre `${exe}/Fonts`, `$HOME/.fonts` e `/usr/share/fonts`.
   - No aparelho só 3 fontes foram copiadas para `$HOME/.fonts`.
   - A fonte padrão de um recurso novo é `"Arial"`, que não existe.
4. **Abrir links, manual (F1) e "Show in Explorer".**
   - Os links (`IDE.OpenBrowser`) não abrem nada e falham sem aviso.
   - "Show in Explorer" (`FileSystem.OpenLocation`) lança exceção não tratada, com provável fechamento da IDE.
   - O mesmo acontece no fim da exportação YYZ e de outros fluxos que abrem a pasta no fim.
5. **Reiniciar a IDE** (trocar skin/DPI/runtime/vsync/Code Editor 2): a IDE fecha e não volta, porque
   `IDE.SpawnNewIDE` executa `<ide>/IDE`, que não existe.
6. **Preview de Texture Group e preview de sprite Spine/SWF não funcionam.** Esses fluxos chamam o
   `GMAssetCompiler` como executável (`IDE.GetAssetCompilerEXEPath()`), fora do Igor.
7. **Gerenciador de pacotes (gmpm/Marketplace):** o Node.js embutido é o do Windows, então o gmpm não inicializa.
   Também depende de rede/conta.
8. **Sem diferença por plataforma** nos editores de sprite (desenho), objeto, room, sequência, partículas,
   tileset, path, shader, código e GML Visual. O que muda é a entrada por toque (`TouchInput`/`ShortcutBar`).
   Não fiz teste funcional deles no aparelho; ver o roteiro da seção 4.

---

## 2. Tabelas por área

Legenda de esforço: **P** = até meio dia, **M** = 1–2 dias, **G** = mais que isso.

### 2.1 Rodar, compilar e depurar (Build.dll, Debugger.dll, Igor)

| Função | Status | Causa confirmada | Correção sugerida | Esforço |
|---|---|---|---|---|
| Run (F5) / Debug (F6) | **quebrado** | `docs/run-preview-analise.md`: `Common.CheckLinuxPlatformSettings` (ícone Linux) → `Common.WriteToBuildFile` → `WinTrust.VerifySecurity` (P/Invoke `wintrust.dll`, sem patch em `BuildPatches.cs`) → `RunLinux` | Plano P1–P8 daquele documento (em andamento) | G |
| Build > Clean | **quebrado** (mesma trava) | `Build.dll` ~l.16266: `Common.WriteToBuildFile(dictionary, "<plataforma> Clean", ProcessComplete)`, com `text2 = TargetManager.TestPlatform_Actual` = `linux`. Passa pelo mesmo `WinTrust.VerifySecurity` | Coberto pelo P1 (patch do `VerifySecurity`). Depois, conferir se `-- Linux Clean` do Igor só apaga o cache | P (após P1) |
| Create Executable / Package Android | **funciona** (fluxo local) | `docs/apk-export.md`. Chama o stub direto, sem `WriteToBuildFile` | — | — |
| Outras plataformas (Windows, HTML5, GX, etc.) | **não suportado** | Igor sem `/bin/bash` e runtime sem os módulos; GX fora do popup (`PatchAndroidFirstTarget`) | — | — |
| Debugger (janela) | **não verificado** | Depende do F6. Linha `[Debugger]target=…` no Output (`docs/run-preview-analise.md` §4) | Testar depois do Run local (T6 daquele plano) | — |
| Preview de Texture Group (Game Options > Texture Groups > Preview) | **quebrado**, sem aviso na UI | `Build.dll` `TexturePageGadget.PreviewClicked` (~l.27150) executa `new CmdProcess(IDE.GetAssetCompilerEXEPath(), …).RunAsync()`. `IDE.GetAssetCompilerEXEPath()` tira o `.dll` de `${asset_compiler_path}` no Linux. Pelo log, essa preferência é `…/build-tools/ac-…-v3/GMAssetCompiler.dll` → executa `…/GMAssetCompiler`, que não existe → `CmdProcess` captura → `"Error '-1' creating texture page preview"` no log. Na sequência, `FileSystem.OpenLocation(destdir)` (ver 2.4) | Patch em `IDE.GetAssetCompilerEXEPath` para devolver um link `GMAssetCompiler` → `libgmexec.so`, como o `Igor`, e rotear no `:build` (`ToolBridge`) para o GMAssetCompiler em processo. Trocar o `OpenLocation` final por um visualizador | M |
| Slim/assets-used de prefab (`IDE.GetAssetsUsedFileFromAssetCompiler`) | **quebrado** (mesma causa) | `IDE.dll` ~l.1452: `new CmdProcess(IDE.GetAssetCompilerEXEPath(), " /c /m=… /assetsUsed=…")` | Mesmo roteamento acima | P (junto) |
| Conversão de áudio na compilação do jogo | **parcial** (corrigido, não testado no aparelho) | `IgorPatches.PatchAudioTools` troca `GMSound.DoFFMPEG/DoFFProbe` para `libffmpeg.so`/`libffprobe.so` (nota ffmpeg-android: testado só no PC) | Testar no aparelho (roteiro, passo 8) | — |

### 2.2 Som (Audio.dll, SoundEditor.dll, AudioTextureGroups.dll, yyal)

| Função | Status | Causa confirmada | Correção sugerida | Esforço |
|---|---|---|---|---|
| Importar arquivo de som (WAV/OGG/MP3) | **funciona, provável** | Diálogo SAF (`AndroidOpenFileDialog`). Cópia gerenciada | — | — |
| Play/Stop no editor de som | **quebrado** | `Audio.dll` `Cache.Init()`: no `OSType == 2`, `ffMpegDir = "/usr/bin/"` → `ffMpeg = "/usr/bin/ffmpeg"` (REA `ev_384f8641…`). Todo Play passa por `AudioPlayer.PrepareToPlay()` → `await Cache.Convert(s)` → `ConvertInternal` → `proc.Start()` → `Win32Exception` → `ConvertInternal(3)` → `ConversionResult.Failed` → `OnConversionFailure`. O `libyyal.so` (stub AAudio) abre bem (log `yyal: AAudio aberto: 48000 Hz`), mas nunca recebe arquivo | Patch Cecil em `Cache.Init` (ou no `ldstr "/usr/bin/"`) para apontar `ffMpeg` para `<nativeLibraryDir>/libffmpeg.so`, já no APK | P |
| Forma de onda (gráfico) no editor de som | **quebrado** | `Cache.ConvertImage` usa o mesmo ffmpeg com `-filter_complex "aresample=ochl=mono,showwavespic=s=450x150…"` e saída `.tiff`. O ffmpeg do APK (`native/ffmpeg/build-ffmpeg.sh`) tem só `FF_FILTERS="aresample,aformat,anull,atrim,asetnsamples"`, `FF_ENCODERS="pcm_s16le,pcm_u8,libvorbis,libmp3lame"` e `FF_MUXERS="wav,ogg,mp3"`: faltam `showwavespic`, o encoder `tiff` e o muxer `image2` | Reconstruir o ffmpeg com `--enable-filter=showwavespic --enable-encoder=tiff --enable-muxer=image2` (e `--enable-protocol=file`, se o script não tiver) + o patch acima | P |
| Pré-escuta de efeitos/buses/3D | **parcial** | `native/yyal-stub/yyal_stub.c`: grupos, buses, efeitos, listener, falloff e posição 3D são no-op | Só se fizer falta | M |
| Audio Groups (atribuir sons a grupos) | **funciona, provável** | Edição de dados. O preview usa o mesmo `Audio.Cache` | Depende do patch de ffmpeg acima | — |

### 2.3 Fontes (Utils.dll, Graphics.dll, FontEditor.dll)

| Função | Status | Causa confirmada | Correção sugerida | Esforço |
|---|---|---|---|---|
| Lista de fontes do Font Editor | **parcial** (poucas fontes) | `FontEditor.UpdateFontList` usa `FontManager.GetFontFamilyNames()` = `m_fontFiles` (sem as internas) + `SystemFontCollection` (vinda de `FontManager.AvailableFonts` → `SystemFonts.GetSystemFonts()`). No Linux, `SystemFonts.GetLinuxSystemFonts()` é `return new List<ISystemFontFamily>();` (log: `Discovered system fonts via kernel in 0ms`). `m_fontFiles` vem de `IDE.ValidateOsSpecificFontFolders`: `${exe}/Fonts` (só as sem "icon"), `SpecialFolder.Fonts` (= `$HOME/.fonts`) e `"/usr/share/fonts"`. `Launcher.InstallFonts` copia só 5 nomes fixos, e no aparelho existiam 3 (log `Fontes em $HOME/.fonts: DroidSansMono.ttf, CutiveMono.ttf, Roboto-Regular.ttf`). Resultado: Open Sans, Oswald, Droid Sans Mono, Cutive Mono e Roboto | Trocar `"/usr/share/fonts"` por `"/system/fonts"` (`ldstr`) em `IDE.ValidateOsSpecificFontFolders` e em `Graphics.FontManager.Initialize` (2 patches Cecil). Ou copiar/ligar todos os `.ttf/.otf` de `/system/fonts` em `Launcher.InstallFonts` | P |
| Fonte nova (padrão `Arial`) | **diferente** | `IDE.dll` ~l.40094, criação de `GMFont` com `fontName = "Arial"`. O Android não tem Arial, então `FontManager.IsFontAvailable` dá falso e aparece o aviso de fonte ausente | Escolher outra fonte (roteiro). Opcional: mapear "Arial" para Roboto ou Liberation Sans, se for copiada | P |
| Gerar glifos/textura da fonte | **não verificado** | Feito pelo SharpFont (`freetype.dll` → `libfreetype.so`, resolvido no log). Não vi uso de API do sistema | Testar (roteiro, passo 7) | — |
| Fonte da própria IDE (Preferences > Font) | **parcial** | `InstalledFonts` usa a mesma lista | Mesmo patch | — |

### 2.4 Abrir links, pastas e arquivos externos (OSCore, IDE, plugins)

| Função | Status | Causa confirmada | Correção sugerida | Esforço |
|---|---|---|---|---|
| Help > Manual / F1 / ajuda contextual | **quebrado** (nada abre, sem aviso) | `HelpPlugins.ShowBrowserWindow` → `IDE.OpenBrowser(url)`. No `case 2`, sem `/usr/bin/open`, faz `Process.Start("xdg-open", "\"url\"")` → `Win32Exception` → `catch` → log `IDE.OpenBrowser Exception`. O manual local (`http://127.0.0.1:<WebServerPort>/index.htm`) também exige baixar o manual (rede/RSS) | Patch em `IDE.OpenBrowser` (ramo Linux) chamando um `GmIde.Platform.OpenUrl(url)` com `Intent.ActionView` (ou Custom Tab / WebView próprio, como o `GameRunnerActivity`) | P |
| Links da Start Page, Marketplace, "What's new", contato, fórum | **quebrado** (mesma causa) | `IDE.OpenBrowser` em `StartScreen`, `MyAccount`, `LocalPackage`, `BugReporter`, `SettingsEditor`, `CodeEditor2` e `Build` | Mesmo patch | — |
| Help > Open project directory / log directory | **quebrado** | `HelpPlugins` chama `IDE.OpenBrowser(${project_dir})` | Mesmo patch (abrir pasta com `DocumentsContract` ou só mostrar o caminho) | P |
| Asset Browser > Show in Explorer | **quebrado**; provável fechamento da IDE | `AssetBrowser` ~l.2493: `FileSystem.OpenLocation(...)` → `NativeFileAPI.OpenFileExplorerAtLocation` → `FileExplorer_Linux.OpenLocation` (OSCore): `Process.Start(new ProcessStartInfo(dir){UseShellExecute=true})`, **sem try/catch** → `Win32Exception` ENOENT (sem `xdg-open`) | Patch em `FileExplorer_Linux.OpenLocation`/`OpenFile` (OSCore) para chamar o host: Intent `ACTION_VIEW` em `DocumentsContract` (pasta) ou `FileProvider` (arquivo), com try/catch e aviso "caminho copiado" como reserva | P |
| Fim da exportação YYZ | **parcial**: o arquivo é gerado, mas o estado não é limpo | `YYZImportExport.DoCompression`: no sucesso chama `FileSystem.OpenLocation(...)` **antes** de `Deactivate()` e `SourceControl.UnPause()` → a exceção pula as duas chamadas | Mesmo patch do `OpenLocation` | — |
| "Open" depois de criar Local Package, BugReporter zip, pasta de build, Included Files, Prefab | **quebrado** | `LocalPackage` ~l.797, `BugReporter` ~l.1350, `Build` ~l.15742/27158, `IncludedFilesEditor` ~l.804 e `PrefabPlugin` ~l.3704 → `FileSystem.OpenLocation` | Mesmo patch | — |
| Editores externos (Bitmap/SWF/SVG/Spine/Sound) e "Open in associated application" | **quebrado** | `ExternalEditors`: `CmdProcess(editor, …)` com caminho das Preferências, ou `OpenInAssociatedApplication` → `Process.Start(UseShellExecute)` (sem `xdg-open`). Usado por `IncludedFilesEditor` ~l.945/1076 | Mesmo patch (`ACTION_EDIT`/`ACTION_VIEW` com `FileProvider`). Volta exige recarregar o arquivo (o `FileSystemWatcher` da IDE já observa) | M |
| Preview de sprite Spine/SWF | **quebrado** | `ImageEditor` ~l.3192/3267: `new CmdProcess(IDE.GetAssetCompilerEXEPath(), …)` | Mesmo roteamento de 2.1 | — |

### 2.5 Editores de recurso e código

Nenhum desses plugins tem P/Invoke nem `Process.Start` (varredura completa). Os ramos `Platform.OSType`
encontrados são só de tecla:

- Room/Image: `Delete` com Shift fora do Mac (`RoomEditor` ~l.11477/12374/14921, `ImageEditor` ~l.27065).
- CodeEditor2: Ctrl em vez de Meta (~l.10794); `NextTokenIndex` usa a implementação Windows.

| Função | Status | Causa / observação | Correção sugerida | Esforço |
|---|---|---|---|---|
| Sprite Editor + Image Editor (desenhar, camadas, importar imagem, strip) | **não verificado** (sem dependência de plataforma) | ImageMagick carregado (`libMagick.Native-Q8-arm64.so` no log). Importar usa SAF. Pressão de caneta: `Graphics.DriverInterface` (Wintab32) só em `try`, então falha sem efeito, como no Linux | Testar com toque e com S-Pen | — |
| Colar imagem do sistema no Sprite Editor | **diferente** (igual ao Linux) | `IDE.SystemClipboard.LinuxSystemClipboard.GetImage()` devolve `null` e `SetImage` não faz nada. Texto usa `SDL_GetClipboardText`/`SDL_SetClipboardText` (funciona no Android) | Opcional: imagem pelo `ClipboardManager` do Android | M |
| Object Editor (eventos, propriedades, física) | **não verificado** | Gerenciado puro | Roteiro, passo 4 | — |
| Editor de código (CodeEditor2) + Language Server/Feather | **parcial / não verificado** | LSP em processo pelo stub (`ToolBridge`, log `Language Server: GML.lspConfig -> …/libgmexec.so`). `CodeEditor2.GetCacheNodePath()` só serve para lspConfigs com `${NodePath}`, e o `GML.lspConfig` não usa | Testar autocompletar e erros (roteiro, passo 5) | — |
| GML Visual (arrastar blocos) | **não verificado** | Arrastar com toque passa pelo filtro do `TouchInput` | Roteiro, passo 5b | — |
| Room Editor (camadas, instâncias, tiles, pan/zoom) | **não verificado** | Pan com botão do meio → modo Mover da barra (`MobileWindows`). Excluir camada exige `Delete` sem Shift | Roteiro, passo 6 | — |
| Sequence (Alice), Particle, Tileset, Path, Timeline, Shader, Notes, Anim Curve | **não verificado** | Sem dependência de plataforma | Teste exploratório | — |
| Extensions Editor | **parcial** | Só a função `AddFramework_Mac` e `-- Mac CopyFramework` usam processo (Mac). Extensões Java/Kotlin não entram no APK (`docs/apk-export.md`, Limites) | — | — |
| Included Files: abrir arquivo | **quebrado** | Ver 2.4 (`OpenInAssociatedApplication`) | — | — |

### 2.6 Projeto: abrir, salvar, importar e exportar

| Função | Status | Causa confirmada | Correção sugerida | Esforço |
|---|---|---|---|---|
| Novo projeto por template / abrir / salvar | **funciona** | Notas template-hang-fix e unsafe-location (`IdePatches.PatchUnsafeLocalAppData`) | — | — |
| Diálogos abrir/salvar/pasta | **funciona** (SAF) | `host/AndroidFileDialogs.cs` (caminho real com "Todos os arquivos" ou cópia/espelho) | — | — |
| Importar YYZ | **não verificado** | `YYZImportExport`: extração com `ZipUtil.ExtractArchive` + `ProjectTool.Run("PROJECT SAVE …")` (stub em processo) | Testar | — |
| Exportar YYZ | **parcial** | `ProjectTool.Run("EXPORT PACKAGETYPE=PROJECT …")` funciona pelo stub (não testado). Depois tropeça no `OpenLocation` (2.4) | Patch do `OpenLocation` | — |
| Arrastar arquivos para a IDE | **não disponível** | `OSCore` `Win32DropTarget` (ole32/shell32) é só Windows. No Linux a IDE usa `SDL_DROPFILE`, e o SDL Android não gera esse evento em uso normal | Usar Import/diálogos | — |
| Reiniciar a IDE (pedido por skin, DPI, vsync, Code Editor 2, troca de runtime, canal beta) | **diferente**: a IDE fecha e não volta | `request_restart_ide` (`ProgramFlowDialogs`) → fim do `IDE.Run()` com `m_isRestarting` → `SpawnNewIDE` no `case 2`: `Process.Start(GetPathToExecutable(), …)`, onde `GetPathToExecutable()` = `Assembly.GetEntryAssembly().Location.Replace(".dll","")` = `…/ide/IDE` (não existe) → `Win32Exception` fora de qualquer catch interno → `IDE.Execute` (catch) | Patch em `IDE.SpawnNewIDE` (`case 2`) chamando o host para recriar a activity (`Intent` com `FLAG_ACTIVITY_NEW_TASK` + `Process.killProcess`, ou `ProcessPhoenix`) | M |
| "New IDE window" / abrir prefab em outra IDE | **não suportado** | Mesmo `SpawnNewIDE` (`PrefabPlugin` ~l.3868, `ProgramFlowDialogs.SpawnNewIDE`) | Desativar o item, ou abrir no mesmo processo | P |

### 2.7 Rede, conta e pacotes (só registro, sem contornar licenciamento)

| Função | Depende de | Comportamento sem login / sem rede (observado ou estático) |
|---|---|---|
| Login / My Account | conta YoYo | Mapa em `docs/login-entrypoints.md`. Menu Build e Create Executable seguem `WindowManager.SetLicenseLimitedState` |
| Marketplace / Package Manager (gmpm) | rede + Node.js | Log: `GMPM: Assembly setup successful`, depois `Removing stale node installation …/node/node`. `GmpmBindings.ExtractNodePackage` extrai `gmpm/node-v24.14.1.zip`, que é o **Node do Windows** (`node/…`, `.cmd`, `.bat`). `ValidateNodeCache` procura `node/bin/node` no Linux e não acha → `InitialiseGMPMWithNPM(string, string)` nunca é chamado e `gmpmInitialised` fica falso → **provável** falha ao instalar pacotes (não testado no aparelho). Mesmo com um Node arm64-bionic, o registro é online (`gmpm-config.gamemaker.io`) |
| Idiomas da IDE | pacotes gmpm | Log: `File exception … gmpm-lts/Languages`. Fica só em inglês |
| Download do manual (F1 offline) | rede (RSS) | `HelpPlugins.DownloadManual`. Mesmo baixado, abrir esbarra no `OpenBrowser` (2.4) |
| Notícias e imagens da Start Page | rede | `NetFix.cs`: TLS funciona. No log, falha de DNS por falta de rede naquele momento |
| Baixar/atualizar runtime | conta + feed | Depois do download, `IDE.dll` ~l.35065 executa `CmdProcess("/bin/sh", "-c \". …/linux-post-install.sh\"")`. No Android é `/system/bin/sh`, então o script não roda. Usar o runtime importado (`RuntimeImport.cs`) |
| Bug Reporter | rede | Não testado. `OpenLocation` do zip cai em 2.4 |
| MQTT interno da IDE | — | Funciona (log `MQTT - Received msg …`) |
| Opera GX / Reddit publish | conta | Fora do popup (`PatchAndroidFirstTarget`) |

### 2.8 Controle de versão (SourceControlGit)

| Função | Status | Causa confirmada | Correção sugerida | Esforço |
|---|---|---|---|---|
| Git (init, commit, status, diff, merge) | **quebrado** | `SourceControlGit.ExecuteGitCommand`: `new ProcessStartInfo(Preferences.GitExecutablePath, …)`. O padrão no Linux é `"git"` (`SetGitExecutablePath`), e o aparelho não tem git (`command -v git` → ausente). Diff/merge externos (`DiffToolPathOptionsSetting`, `MergeToolPathOptionsSetting`) também são executáveis | Embutir um `git` arm64 estático (como o ffmpeg) e apontar a preferência para ele. Ou usar LibGit2Sharp em processo (exige reescrever chamadas) | G |

### 2.9 Sistema, entrada e diversos

| Função | Status | Causa | Observação |
|---|---|---|---|
| Copiar/colar texto | **funciona, provável** | `SystemClipboard.GetText/SetText` → SDL | Não testado entre apps |
| Mesa digitalizadora (pressão) | **não disponível** | Wintab32 só Windows (try/catch em `Graphics.WinTabContext`) | Igual ao Linux |
| Som de erro do sistema | no-op | `OSCore.SystemErrorSound.LinuxPlaySystemErrorSound()` tem corpo vazio | Igual ao Linux |
| Versão do SO no Bug Reporter/Analytics | diferente | `OSCore` `Platform` (Linux): `Process.Start("lsb_release")` em `try/catch` vazio → "Linux kernel …" | Inofensivo |
| Contagem de instâncias da IDE | **funciona** | `IdePatches.PatchProcessHelperShell` (`/system/bin/sh`) | — |
| Escala, toque, teclado virtual, docks | **funciona** | `IdeSetup`, `TouchInput`, `KeyboardMode`, `MobileWindows`, `ShortcutBridge` | — |
| `glxinfo` (aviso de drivers MESA) | inofensivo | `OSCore.CheckForBadMESADrivers` em `try` | — |

### 2.10 P/Invoke dos assemblies da YoYo × `host/NativeResolver.cs`

Módulos Windows/macOS que não estão no `NativeResolver.Map`. Todos foram conferidos na decompilação.

| Assembly | Módulo / função | Quem chama | Roda no Android? | Precisa de mapa? |
|---|---|---|---|---|
| Build | `wintrust.dll` `WinVerifyTrust` | `WinTrust.VerifySecurity` ← `Common.WriteToBuildFile` | **sim** (sem gate) | Não: substituir o método (P1 do Run) |
| IDE | `wintrust.dll` `WinVerifyTrust` | `SecurityChecks.VerifyDLLs` | não: `IntegrityCheck.ReplaceVerifyDlls` esvazia `ms_files` | Não |
| IDE | `user32`/`kernel32` (clipboard) | `WinSystemClipboard` | não: `SystemClipboard` (cctor) escolhe `LinuxSystemClipboard` no `OSType 2` | Não |
| IDE | `ole32` `CLSIDFromProgID[Ex]`, `oleaut32` `GetActiveObject` | `YoYoStudio.Utils.Marshal2` | sem chamadores nos assemblies decompilados | Não |
| IDE | `libc` `exit` | `YoYoStudio.User.Receipt` | bionic (`libc` mapeada) | Já mapeado |
| OSCore | `user32` (`GetDesktopWindow`, `MonitorFromPoint`, `GetWindowRect`, `GetRawInputDeviceList`, `ScreenToClient`), `shcore` `GetDpiForMonitor`, `ole32`/`shell32` (drop), `kernel32` (`GetModuleHandle`, `GetProcAddress`, `GetSystemPowerStatus`, `GetUserPreferredUILanguages`), `winmm` `PlaySound` | `WindowsDisplayDpi`, `MouseCursor`, `Win32DropTarget`, `Platform`, `WindowsLaptop`, `SystemLanguage`, `SystemErrorSound` | não: ramos `OSType == 0` (conferido em `SystemErrorSound` e no `NativeFileAPI`); nenhum `NAO resolvido` nos logs | Não |
| OSCore | `tinyfiledialogs` (18) | `Core.CoreOS.System.tinyfd` | sim | Já mapeado (`libtinyfiledialogs.so`) |
| OSCore | `libc` `prctl`, `uname` | `SystemPlatform`, `PlatformProvider` | sim | Já mapeado |
| Utils | `gdi32` (8, `SystemFonts`) | `GetWindowsSystemFonts` | não (`GetSystemFonts` despacha por `OSType`) | Não; o problema é a lista Linux vazia (2.3) |
| Utils | `libc` `sysctlbyname` | `Analytics` (macOS) | não | — |
| Graphics | `Wintab32` (15) | `DriverInterface`/`WinTabContext` | tenta e cai no `try/catch` | Não |
| FileIO, Feather.Ui, GMLS | `Shlwapi` `PathFileExists`/`PathIsDirectory` | `FileIO`, `InactiveDocumentHandler` | não: `if (Platform.OSType == 0)` (conferido em `Feather.Ui` ~l.2303) | Não |
| GMLS | `ntdll` `NtQueryInformationProcess`, `libc` `getppid` | `ParentProcessUtilities`, `LinuxMonitor` | não: `ToolBridge` não chama `ParentProcessMonitor.Initialize` | Não |
| Audio / CoreResources | `yyal.dll` (47 + 1) | `NativeMethods`, `YYAL.Result` | sim | Já mapeado (stub próprio) |
| CodeEditor2 | `onig.dll` (10) | Oniguruma | sim | Já mapeado + `SeedFnaDllMapper` |
| DebugInfo | `IDENativeLib` | `DieDieDie` (comando de teste de crash) | só por comando | Já existe stub |
| GameMaker-LTS2026 | kernel32/user32/winmm/objc | lançador original | não carregado pelo port | — |

**Conclusão P/Invoke:** fora o `wintrust.dll` do Build.dll (já conhecido), nenhum P/Invoke Windows dos assemblies da
YoYo está num caminho que roda com `OSType == 2`. Não falta mapeamento no `NativeResolver`. As falhas reais estão em
**processos externos** (seção 2.4) e em **implementações Linux vazias** (fontes, clipboard de imagem).

### 2.11 Integridade e assinatura

| Verificação | Situação |
|---|---|
| `IDE.dll` `SecurityChecks.VerifyDLLs` (6 DLLs, WinVerifyTrust) | Substituída por `host/IntegrityCheck.cs` (hash do PC em `ide-integrity.json`) |
| `Build.dll` `WinTrust.VerifySecurity(${igor_path})` | **Sem patch.** Ver `docs/run-preview-analise.md` (P1). Substituto pronto: `BuildBridge.VerifyBuildTools` |
| Checksum de assembly no `.gmplugin` (DLLs com patch) | `Launcher`: pré-carrega as DLLs com marcador `.gmide-patch` |
| Igor / GMAssetCompiler / GMWebServer | `RuntimeIntegrity.cs` (manifesto do PC) |
| ProjectTool / Language Server | `gmide-tools-integrity.json` (`ToolBridge`) |
| Outras | A busca por `WinTrust.`, `VerifySecurity(`, `X509Certificate.CreateFromSignedFile`, `SignedCms` e `Authenticode` não achou mais nada |

---

## 3. Lista priorizada (impacto ao criar um jogo simples)

| # | Item | Por quê | Esforço |
|---|---|---|---|
| 1 | Run/Debug local (plano do `docs/run-preview-analise.md`) | Sem isso, cada teste vira exportar + instalar APK | G (em andamento) |
| 2 | Sound Editor: ffmpeg do `Audio.Cache` → `libffmpeg.so` + rebuild com `showwavespic`/`tiff`/`image2` | Sem ouvir o som não dá para ajustar volume nem cortes | P |
| 3 | Fontes: `/system/fonts` em `ValidateOsSpecificFontFolders` + `FontManager.Initialize` | Font Editor com 5 fontes e "Arial" ausente na fonte nova | P |
| 4 | `FileExplorer_Linux.OpenLocation/OpenFile` + `IDE.OpenBrowser` → Intents do Android | Evita fechamento da IDE em "Show in Explorer" e no fim do YYZ; F1 e links passam a abrir | P |
| 5 | `IDE.SpawnNewIDE` (`OSType 2`) → reinício pelo host | Mudar skin/DPI/runtime fecha a IDE | M |
| 6 | `GetAssetCompilerEXEPath` → stub (Texture Group Preview, Spine/SWF, prefab slim) | Recurso secundário para jogo simples | M |
| 7 | Editores externos / Included Files → `ACTION_EDIT`/`ACTION_VIEW` | Conveniência | M |
| 8 | Git embutido | Opcional | G |
| 9 | gmpm/Node | Exige Node bionic e rede/conta; fora do fluxo básico | G |

Também vale atualizar a documentação: vários comentários citam `docs/pinvoke-audit.md`, `docs/build-pipeline.md`,
`docs/external-tools.md`, `docs/touch-ui.md` e `docs/input-analysis.md`, e nenhum deles existe em `docs/`.

---

## 4. Roteiro de teste manual no aparelho: jogo simples do zero

Objetivo: um jogador que anda com as setas, coleta moedas com som e mostra a pontuação com uma fonte própria.
Exportar como APK e jogar. Anote o resultado de cada passo (ok / falhou / diferente). Para coletar o log:

```bash
export MSYS_NO_PATHCONV=1
```

```bash
"$LOCALAPPDATA/Android/Sdk/platform-tools/adb.exe" -s "adb-53e1eb7-cDkkst (2)._adb-tls-connect._tcp" logcat -v time GmIde:* GmIde.Native:* GmIde.Tools:* GmIde.Build:* GmIde.Igor:* DOTNET:* yyal:* AndroidRuntime:E *:S
```

(Sem adb: `Download/gmide-log.txt`.)

**Não use ainda:**

- **Show in Explorer**, **Help/F1** e links: risco de fechar a IDE ou de nada acontecer (2.4).
- **Reiniciar a IDE** pelas Preferências: ela fecha (2.6).
- **F5/F6**, até o Run local entrar (2.1).

1. **Projeto novo.** Start Page → New → *Blank Pixel Game* (ou *Blank Game*) → nome `TesteZero` → Let's Go.
   Esperado: abre sem o aviso "unsafe location", com Room1 no Asset Browser. Salvar (Ctrl+S pela barra de atalhos).
2. **Sprite do jogador.** Asset Browser → clique direito (toque longo) → Create → Sprite → `spr_player`.
   - Edit Image: desenhe um quadrado 32×32 com o lápis (toque e, se tiver, caneta). Teste desfazer e zoom (pinça/roda).
   - Defina a origem no centro.
   - Repita para `spr_moeda`.
   - Teste Import: use um PNG do armazenamento (seletor do sistema) num terceiro sprite.
   - Esperado: desenho e importação funcionam. Colar imagem copiada de outro app não funciona (esperado, 2.5).
3. **Som.** Create → Sound → `snd_moeda` → importar um `.wav` ou `.ogg` curto.
   - Toque em Play. **Hoje esperado: não toca e não mostra forma de onda** (2.2). No log aparece `ConvertInternal(3)`
     com `Win32Exception`.
   - Anote isso; não bloqueia o resto.
4. **Objetos.**
   - `obj_player`: sprite `spr_player`, evento Step com movimento por `keyboard_check(vk_left/right/up/down)`, e evento
     Collision com `obj_moeda` (`score += 1; audio_play_sound(snd_moeda, 1, false); instance_destroy(other);`).
   - `obj_moeda`: sprite `spr_moeda`.
   - `obj_hud`: evento Draw GUI com `draw_set_font(fnt_score); draw_text(16, 16, "Score: " + string(score));`.
   - Esperado: adicionar eventos pelo botão Add Event, menus abrindo com toque longo.
5. **Editor de código.** Ao digitar no Step:
   - O teclado virtual abre e o editor fica em "tela cheia" (`KeyboardMode`).
   - Aparece o autocompletar e os erros do Feather (sublinhado em `keyboard_chek`, por exemplo). No log, `GmIde.Tools`
     deve mostrar o Language Server iniciando.
   - Teste copiar/colar texto.
   - 5b. Opcional: crie um evento em GML Visual e arraste dois blocos.
6. **Room.** Abra Room1:
   - Na camada Instances, arraste `obj_player`, várias `obj_moeda` e um `obj_hud`.
   - Use o modo **Mover** da barra para pan/zoom.
   - Apague uma instância com Delete (sem Shift).
   - Mude o tamanho da room em Properties.
7. **Fonte.** Create → Font → `fnt_score`.
   - **Esperado hoje:** "Arial" marcada como ausente e lista curta (Open Sans, Oswald, Droid Sans Mono, Cutive Mono,
     Roboto) (2.3).
   - Escolha **Roboto**, tamanho 16, e confira a prévia dos glifos.
8. **Game Options.**
   - Engrenagem → Android → General: nome e pacote `com.teste.zero`.
   - Orientação: só Landscape, como em `docs/apk-export.md`.
   - Não use **Texture Groups > Preview** (2.1). Se usar, nada aparece e o log mostra `creating texture page preview`.
9. **Gerar APK.** Alvo Android → Gerar APK → nome `TesteZero`.
   - Esperado: sucesso em `Download/TesteZero.apk`.
   - No log do `:build`, a conversão do `snd_moeda` sem erro de ffmpeg (`DoFFMPEG` → `libffmpeg.so`). É o primeiro
     teste no aparelho desse caminho.
10. **Jogar.** Instale o APK (confirmação do Android) e abra. Confira:
    - o jogador se move (teclado físico ou setas na tela; num celular sem teclado, troque o movimento por toque
      com `mouse_check_button`);
    - as moedas somem com som;
    - o texto do placar sai com a fonte Roboto;
    - a orientação está certa.
11. **Salvar e reabrir.** Feche a IDE pelo menu (File > Exit) e reabra.
    - Esperado: o projeto volta igual, com as Game Options Android preservadas
      (`AndroidOptionsPatches`/`local-android-options-load-v1`).
12. **Exportar YYZ (opcional, risco conhecido).** File > Export Project → YYZ.
    - Esperado hoje: o arquivo é criado, mas a IDE pode fechar ou ficar com a exportação "presa" no fim (2.4/2.6).
    - Confira se o `.yyz` existe no destino.

Resultado esperado hoje:

- Passos 1, 2, 4, 5, 6, 8, 9 e 11: ok ou com diferenças só de toque.
- Passo 3: sem pré-escuta.
- Passo 7: lista curta de fontes.
- Passo 10: é a primeira validação no aparelho do áudio compilado e da fonte gerada.
- Passo 12: arquivo criado com falha no fim.
