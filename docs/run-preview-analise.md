# Run (F5) no aparelho: diagnóstico e plano

Data: 07/11/2026. Só investigação: nenhum arquivo do projeto foi alterado e nenhum build foi feito.
Assemblies analisados:

| Artefato | SHA-256 (início) | Origem |
|---|---|---|
| `C:\Program Files\GameMaker-LTS2026\Build.dll` | `9e47dc05b6c2…` | IDE 2026.0.0.16, MVID `4f7d5454-…` |
| `Igor.dll` (bundle de `runtime-2026.0.0.23/bin/igor/windows/x64/Igor.exe`) | `0c004a718235…` | igual ao `tools/build-test/igor-bundle/Igor.dll` (conferido: mesmo hash) |
| `runtime-2026.0.0.23/android/runner/.../arm64-v8a/libyoyo.so` | `9f41bff27d04…` | runner nativo Android |
| `tools/apk-template/work/io.gmide.yyrunner/src/...` | — | fonte Java do runner expandido pelo `make-template.py` |

Ferramentas: `ilspycmd` 8.2 (Build.dll, Igor.dll, IDE.dll/Utils.dll por tipo) e REA (`inspect_managed_members`,
`inspect_native_exports`). O provedor JADX do REA não está configurado (`REA_JADX_MCP_JAR`), então as classes Java
do runner foram lidas no fonte que o próprio Gradle do runtime compila no modelo.

---

## 1. Resumo

0. **Erro observado no aparelho (F5 do usuário, 07/11 22:48–22:50, 15 toques):** cada toque gera
   `DOTNET: File exception 'IO_PathNotFound_Path, …/runtime-2026.0.0.23/BaseProject/options/linux/icons/64.png'`.
   O projeto tem `"option_linux_icon":"${base_options_dir}/linux/icons/64.png"`, e o runtime não tem
   `BaseProject/options/linux` (só `android`, `main`, `switch2` e `windows`, no aparelho e no PC), porque o módulo
   linux não está instalado. `Common.CheckLinuxPlatformSettings()` recebe `YoYoImage.FromFilename(...) == null`,
   escreve `open_options_editor,Linux,,,game.Linux.Images.game_icon…$ConfigurationProblem_LinuxIcon` no canal
   AssetCompiler (vai para Compile Errors) e devolve `false`. O `RunTheProgram` imprime
   `FAILED: Run Program Complete` e para. **Esse é o "erro" que o usuário vê:** o Run termina aqui, antes do
   `build.bff`, e nenhuma linha `GmIde.Igor`/`GmIde.Build` aparece. Corrigir só o ícone não basta, por causa do
   item 1. O P2 da seção 6 resolve os dois, porque troca esse teste Linux.
1. **Mesmo passando do ícone, o F5 não chegaria ao Igor.** O alvo do projeto é `Test / VM / Default` e `Test` vira `Linux`. A IDE monta o
   `build.bff`, mas antes de iniciar o `${igor_path}` o `Common.WriteToBuildFile` chama
   `WinTrust.VerifySecurity`, que faz P/Invoke de `WinVerifyTrust` em `wintrust.dll`. No Android essa DLL não
   existe. O substituto `BuildBridge.VerifyBuildTools` foi escrito e testado (`tools/build-test/stub-test`), mas
   **o patch que o liga ao Build.dll não existe mais**: `BuildPatches.cs` só aplica `android-apk-target-v4`.
   A exportação de APK funciona porque `AndroidGameCompile.Run` chama o stub direto e não passa por esse método.
2. **Mesmo com essa trava removida, "Linux Run" não roda no aparelho.** O Igor executa `/bin/bash` (não há no
   Android) e copia `<runtime>/linux/runner.zip`. O runtime do aparelho não tem o módulo `linux`, e o runner
   Linux é ELF glibc.
3. **"Android Run" pelo caminho original também para:** `Common.CheckAndroidPlatformSettings` exige
   `<sdk>/platform-tools`. Além disso, o `Deploy` do Igor chama `IsAndroidDeviceConnected()`, que nosso patch
   força a `false`, e cai em `Program.Exit(-1)`.
4. **Recomendação:** rodar o jogo no próprio app, num processo `:game`, com as classes do runner Android
   (`RunnerActivity`/`RunnerJNILib`/`libyoyo.so`). O F5 passa a executar `-- Android Run` pelo stub; o
   `RunGradle` grava um zip com `assets/game.droid` em vez de montar um APK; o `AndroidRun_Command` abre a
   activity `:game` e repassa o logcat dela para o Output. Sem instalação, com log, Stop e debugger em
   `127.0.0.1`. A opção (a), instalar o APK exportado, fica como alternativa rápida, mas não traz log e pede
   confirmação de instalação.

---

## 2. O que acontece hoje no F5

### 2.1 Alvo

Logcat (`logs` do aparelho, 07/11 22:08): `GmIde.Build: Alvo: Test / VM / Default (Test = Linux)`.

- `TargetManager.TestPlatform_Actual` (IDE.dll): `OSType==1 → "Mac"`, `OSType==2 → "Linux"`, senão `"Windows"`.
  No app `Platform.OSType = 2` (log `GmIde.Setup`).
- O popup de alvos intercepta Android (`BuildTarget.HandleAndroidTargetSelection`, patch
  `TargetSelectButton.TargetSelect_UpdateTargetManager`) e abre a exportação de APK. Então o usuário não
  consegue deixar Android como alvo de Run: o Run sempre sai com `Test`.
- O comentário de `BuildTarget.cs` ("O alvo local padrão vira Windows pelo patch de TestPlatform_Actual
  (BuildPatches.cs)") está desatualizado: esse patch não existe.

### 2.2 IDE (Build.dll, `YoYoStudio.Plugins.Build.RunProgram`)

`RunProgram.Initialise(ModulePackage)` → estado `IDLE` e `Common.BuildActive == Nothing` → `RunTheProgram()`:

1. `Common.CheckProject()`.
2. `MissingRuntimeModuleLoader.CheckModuleForTargetPlatform()` (IDE.dll). `SelectedModule` = `"test"`. O
   teste de licença é pulado para `"test"` (`!IsAllowedByLicence(m) && m != "test"`), e
   `RuntimeEntry.IsModuleInstalledForActiveRuntime("test")` só falha se o feed listar `test` como módulo e o
   `receipt.json` não. Para `android` ou `operagx` sem login, esse método abriria o pedido de login.
3. `Common.CheckSpineLicence("run_program", …)` e `Common.CheckPlatformSettings(runtime, package:false)`. Para
   `Test` vira `"linux"` → `CheckLinuxPlatformSettings()`: com `OSType == 2` não exige dispositivo; exige
   `GMLinuxOptions.option_linux_icon` carregável.
4. `Common.IsDebug = m_debug`, `Common.BuildActive = eBuildType.Run`, `Command.execute("run_start", …)`.
5. Despacho: `"Test" → TestPlatform_Actual = "Linux" → RunLinux(runtime)`.

`RunLinux(string)` → `IDE.SaveProjectIncremental` → `WriteMacros`, `WriteTargetOptions`,
`Preferences.WriteSteamPreferences/WriteLinuxPreferences` → `CreateDefaultEnvironment(runtime)` (+ `verbose`,
`compile_output_file_name = <DestFolder>/<projeto>.win`, `debug`, `rerun`) →
`Common.WriteToBuildFile(dict, "Linux Run", RunComplete)`.

`Common.WriteToBuildFile(Dictionary<string,string>, string, CmdProcess.OnCmdCompletion, bool)`:

```
FileSystem.WriteText(BuildFilename, json, delegate {               // callback de conclusão
    if (!IDE.CurrentUser.InternalDeveloper &&
        !WinTrust.VerifySecurity(MacroExpansion.Expand("${igor_path}"),
                                 new[] { "YoYo Games Ltd", "YOYO GAMES LIMITED" }))
        Log.WriteLine(eLog.Userlevel, "Corrupt or damaged GameMaker build tools.");
    else
        BuildProcess = new CmdProcess("${igor_path}", "-j=N [-ac=/cins] -options=\"build.bff\" -q|-v -- Linux Run", …);
        …RunAsync();
});
```

`WinTrust.VerifySecurity(string, string[]) : bool` (MethodDef `0x06000585`). Arestas de chamada confirmadas pelo
REA (Evidence `ev_41fa9145…df709`, página `ev_53073100…02fd`):

- `VerifySecurity` IL 3 → `System.IO.File.Exists`
- `VerifySecurity` IL 14 → `WinTrust.VerifyEmbeddedSignature` (`0x06000583`)
- `VerifyEmbeddedSignature` IL 33 → `WinTrust.WinVerifyTrust` (`0x06000582`), `[DllImport("wintrust.dll")]`.
  O Build.dll tem uma única linha `ImplMap` (Evidence `ev_0cd0d0e7…a352`): é esta.

`${igor_path}` aponta para `files/bin/Igor` → `libgmexec.so`, que existe. Então `VerifyEmbeddedSignature` é
chamado e o P/Invoke de `wintrust.dll` não resolve (`NativeResolver.Map` não tem entrada para ela).
**Consequência:** o `CmdProcess` não é criado, o stub não é chamado e o processo `:build` não vê nenhum pedido.
`IntegrityCheck.cs` resolve só o caso equivalente no IDE.dll (`SecurityChecks.VerifyDLLs`); o Build.dll tem sua
própria classe `WinTrust`.

**Este passo não foi alcançado no teste do aparelho:** o Run parou antes, no ícone Linux (seção 1, item 0). O
efeito abaixo é previsto pela leitura do código e vale assim que o teste do ícone for contornado.

**Sintoma previsto:** a exceção sai do callback de `FileRequest.Complete`. Esse callback
é chamado por `AsyncQueue.DispatchCompletedRequests` (Utils.dll), que não tem `try/catch` e só marca
`ResultDispatched` e remove o pedido da fila depois que o callback volta. Possíveis efeitos:

- a mesma exceção volta a cada tick do `FileSystem` e trava a fila de conclusões de arquivo (salvar, ler);
- ou o laço da IDE captura e registra a exceção. Não verifiquei quem trata a exceção acima do `FileSystem.Tick`.

Em qualquer caso, `Common.BuildActive` fica em `Run` e `RunProgram.m_state` fica em `RUNNING`. O próximo F5 cai
no diálogo `Build_StopBeforeRun`. Isso combina com o relato "não abre nada e parece dar erro".

Para confirmar no aparelho, com o `io.gmide` em primeiro plano e o usuário apertando F5:

```bash
export MSYS_NO_PATHCONV=1
ADB="$LOCALAPPDATA/Android/Sdk/platform-tools/adb.exe"
"$ADB" -s "adb-53e1eb7-cDkkst (2)._adb-tls-connect._tcp" logcat -v time -T 1 GmIde:* GmIde.Native:* GmIde.Build:* GmIde.Igor:* DOTNET:* AndroidRuntime:E *:S
```

Marcas esperadas: `GmIde.Native: NAO resolvido: 'wintrust.dll' (pedido por Build)`, um `DllNotFoundException`
com `WinTrust.VerifyEmbeddedSignature`, ou a linha `Corrupt or damaged GameMaker build tools.` Nenhuma linha
`GmIde.Igor` deve aparecer. Nesta sessão o adb listava o aparelho como `adb-53e1eb7-cDkkst (2)._adb-tls-connect._tcp`
(o nome sem o "(2)" não foi encontrado). O app em primeiro plano era o Discord, então não houve screenshot nem
toque, e o buffer de logcat atual não tem nenhum Run.

### 2.3 Igor, se a trava fosse removida (`-- Linux Run`)

`Igor.LinuxBuilder.Run()`:

- `PreRunStep(); Compile(false)` gera o `.win` e `<projeto>.zip`, renomeado para `GameAssetsLinux.zip`.
- `Program.s_runningOnLinux = Environment.OSVersion.Platform == PlatformID.Unix && !s_runningOnMac` → no
  Android vale `true` → `SSH.s_putty = new LocalSSH()`, `linux_install_dir = $HOME/GameMakerStudio2`.
- `InstallRunnerOnLinux()` copia `<runtimeLocation>/linux/[arm64/]runner.zip` e `linux/execute.sh`.
- `plink(...)`/`pscp(...)` em `LocalSSH` rodam `Command.ExecuteCommandSync("/bin/bash", "-c '…'")`.
- No final, executa `./<projeto>` (ou `.AppImage --appimage-extract-and-run`) com
  `-debugoutput <dir>/debug.log -output …` e `PumpGameOutputUntilDone` (o log volta ao Output assim).

No aparelho: não existe `/bin/bash`, o `receipt.json` do runtime tem `base`, `base-module-windows-x64`,
`operagx`, `windows` e `android` (sem `linux`), e o runner Linux depende de glibc. **Esse caminho não tem
conserto razoável.**

### 2.4 "Android Run" hoje

- IDE: `RunProgram.RunAndroid(string)` → `WriteMacros`, `Preferences.WriteAndroidPreferences`,
  `Common.WriteAndroidTargetOptions(runtime)` → `WriteToBuildFile(dict, "Android Run", RunComplete)` com
  `compile_output_file_name = <DestFolder>/<projeto>.droid`. Antes disso, `CheckAndroidPlatformSettings()`
  falha sem `sdk_location/platform-tools`, e a mesma trava do WinTrust se aplica.
- Igor: `AndroidBuilder.Run()`:
  - `IsRerun` → só `AndroidRun_Command()`;
  - VM → `Deploy(false)`: `Compile` → `GameAssetsDROID.zip` → `if (!AndroidUtils.IsAndroidDeviceConnected()) Program.Exit(-1)` →
    `DoSplashScreen/DoIcons` → `CreateAndroidEnvironment` (lê `GetKeystorePassword`) → `CreateAPK` (Gradle, que
    vira `ApkBridge.RunGradle`) → `InstallAPKOnDevice` (no-op pelo nosso patch);
  - debug → `Utils.ExtractYYDebugFile(<outputFolder>/GameAssetsDROID.zip)` e
    `Utils.LaunchIDEDebugger(AndroidUtils.DeviceGetIpAddress(), <outputFolder>/game.yydebug)`;
  - `AndroidRun_Command()`: `adb logcat -c`, `adb shell am start -W … <pacote>/.RunnerActivity`, e
    `adb logcat <option_android_logcat> … *:S` em primeiro plano até o jogo sair (é assim que o log chega ao Output).
- `AndroidBuilder.Stop()`: `adb shell am force-stop <pacote>`.

Com os patches atuais (`IgorPatches.PatchAndroid`), `IsAndroidDeviceConnected` devolve `false`, então
`Deploy` termina com `-1`.

---

## 3. Como o runner Android carrega o jogo

- `RunnerJNILib` (pacote Java fixo `com.yoyogames.runner`) faz `System.loadLibrary("yoyo")` e declara
  `static native void Startup(String _apkPath, String _saveFilesDir, String _packageName, int _sleepMargin, boolean dynamicassetdelivery)`.
  O `libyoyo.so` exporta `Java_com_yoyogames_runner_RunnerJNILib_Startup` (`0xa025dc`) e `…_Process` (`0xa03a34`)
  (REA `ev_77ce22c6…bf3`, página `ev_fa8e84f1…eee6`). O nome JNI não depende do pacote do app.
- `NEEDED` do `libyoyo.so`: só `libdl`, `liblog`, `libz`, `libandroid`, `libc`, `libm` (`llvm-readelf -d`).
  Carrega em qualquer processo do app.
- `DemoRenderer.onSurfaceCreated`: `m_saveFilesDir = context.getFilesDir() + "/"`;
  `m_apkFilePath = getPackageManager().getApplicationInfo(context.getPackageName(), 0).sourceDir`.
  No estado `DoStartup`: `RunnerJNILib.Startup(m_apkFilePath, m_saveFilesDir, m_packageName, SleepMargin, UseDynamicAssetDelivery)`.
- O `libyoyo.so` contém as strings `assets/game.droid`, `assets/` e `/options.ini`. O runner abre o **zip**
  passado em `_apkPath` e lê `assets/game.droid` dele.
- `RunnerActivity.checkIsYoYoRunner()` abre `getAssets().open("game.droid")`. Sem ele, lê o meta-data
  `IsBuiltAsYoYoRunner` (o modelo usa `Yes`). Sem esse meta-data, ativa o modo APK Expansion.
- **Não aceita caminho externo.** O caminho vem sempre de `ApplicationInfo.sourceDir`, sem extra de Intent.
  Para usar um zip externo, é preciso mudar `DemoRenderer` (ou `RunnerActivity.doSetup`) no fonte que o
  `make-template.py` compila.
- `RunnerActivity extends AppCompatActivity` usa `R.layout.main`, `R.id.demogl`, `R.string.help_url` e
  `R.string.learn_more` (pacote de recursos `io.gmide.yyrunner`). Ao sair, chama `Process.killProcess(myPid())`
  e `System.exit(0)`. Por isso precisa de processo próprio.
- `RunnerApplication extends MultiDexApplication`, e `onCreate` chama `RunnerJNILib.Init()`. Dentro do nosso app
  o Application é o `MainApplication`; ele teria de fazer essa chamada no processo `:game`.
- O dex do modelo (`gmide-runner-template-slim.apk`, `classes.dex` de 6,3 MB) inclui androidx
  (appcompat 1.7.0, activity 1.9.3 e dependências) e kotlin.
- O `ApkBuilder` já copia `<módulo>/src/main/assets/**` como `assets/…` e confere `assets/game.droid`
  (`ApkBuilder.cs:125-140`). Esse diretório, expandido pelo Igor, é exatamente o que o runner precisa.

---

## 4. Opções

### (a) Compilar para Android, instalar e abrir o APK

Reaproveita a exportação: `AndroidGameCompile` (`-- Android Package`, perfil `GMIDE_LOCAL_ANDROID_EXPORT=1`) →
`ApkBuilder` → `ApkInstaller` → abrir `<pacote>/io.gmide.yyrunner.RunnerActivity`.

- Prós: caminho validado no aparelho (6,7–13,6 s); o jogo roda como no APK final (manifesto, orientação, ícones);
  isolamento total.
- Contras:
  - Cada Run instala um app. `ApkInstaller.java:39` usa `USER_ACTION_REQUIRED`, então toda vez aparece a
    confirmação do Android. `USER_ACTION_NOT_REQUIRED` (Android 12+) evitaria isso nas atualizações de um app que
    a IDE instalou; os requisitos de targetSdk e de "installer of record" precisam ser conferidos na
    documentação do Android.
  - O jogo tem outro UID, então a IDE **não lê o logcat dele** e o Output fica sem `show_debug_message`.
  - Stop exige `ActivityManager.killBackgroundProcesses` (só funciona com o jogo em segundo plano) ou um
    receiver no runner.
  - Chave de assinatura e pacote fixos por projeto; um APK de mesmo pacote com outra assinatura quebra a instalação.
  - Instala/assina mesmo em Run de teste.
- Exige: o mesmo gancho de F5 (seção 6, P1 e P2) e um fluxo "compilar → instalar → abrir" na `ApkExportActivity`
  ou num serviço.
- Debugger: TCP para `127.0.0.1:<DebuggerPort>` entre apps funciona (o modelo tem `INTERNET`).

### (b) Runner dentro do app, processo `:game` (recomendada)

Declarar o `RunnerActivity` do modelo no manifesto da IDE com `android:process=":game"` e passar um zip com
`assets/…`.

- Prós:
  - Sem instalação.
  - Mesmo UID: o processo `:build` lê o logcat do `:game` (um app lê os logs do próprio UID), então o log volta
    ao Output como no `adb logcat` do Igor.
  - Stop por `Process.killProcess` do PID do `:game` (mesmo UID).
  - O crash do jogo não derruba a IDE.
  - `F5` repetido (rerun) pode reaproveitar o zip.
- Contras:
  - Precisa embutir no APK da IDE as classes Java do runner, os recursos `io.gmide.yyrunner.R` e androidx
    (+ cerca de 6 MB).
  - Exige uma alteração pequena no fonte Java do runner (caminho do zip e pasta de saves via Intent), feita no PC
    pelo gerador do modelo.
  - O Mono também sobe no `:game` (o `MainApplication` roda em todo processo), com custo de memória e de partida.
  - Extensões Java/Kotlin próprias continuam fora (mesmo limite da exportação).
  - Orientação e opções de manifesto do jogo não vêm do manifesto. O runner aplica `RestrictOrientation` a
    partir das opções do jogo (visto no teste de exportação), mas a `screenOrientation` declarada fica fixa.
- Exige: ver a seção 6.

### (b2) Variante: "runner host" instalado uma vez

O APK-modelo (`IsBuiltAsYoYoRunner=Yes`, sem `game.droid`) instalado como app próprio, com o mesmo patch Java.
A IDE passa o zip por `ContentProvider` (o runner abriria o `ParcelFileDescriptor` e passaria
`/proc/self/fd/N` ao `Startup`; não verifiquei se o `libyoyo` aceita esse caminho).

- Prós: o APK da IDE não cresce; só uma confirmação de instalação.
- Contras: outro UID (sem logcat, então seria preciso um canal de log próprio); mais peças; o problema do caminho
  externo continua.

### (c) Alvo GX (WebView) já existente

`BuildService.OpenRunner` + `GameRunnerActivity` (WebView com `runner.html`/wasm) estão prontos para
`OperaGX Run`, e o módulo `operagx` está instalado.

- Prós: código do host pronto; `CheckOperaGXPlatformSettings` só expande nomes e devolve `true`.
- Contras:
  - O alvo foi removido do popup (`PatchAndroidFirstTarget` faz `availablePlatforms.Remove("operagx")`) porque
    leva ao fluxo de conta.
  - `CheckModuleForTargetPlatform` pede login para módulos que não são `test`.
  - Roda wasm, não o runner Android (desempenho e comportamento diferentes).
  - Sofre a mesma trava do WinTrust.

### (d) Linux/Test com runner Linux

Descartada (seção 2.3).

### Debugger (F6) e log

- F6 = `run_program` com `Params[0] == "debug"` → `m_debug = true` → `debug=True` no bff. No Igor (Run Android
  ou Linux) isso gera `game.yydebug` (`Utils.ExtractYYDebugFile`) e imprime
  `"[Debugger]target=<host>,yydebug=\"<arquivo>\""` (`Utils.LaunchIDEDebugger`). O `Debugger.dll` contém
  `[Debugger]`, `target=`, `yydebug=` e lê `machine.General Settings.Debugging.DebuggerPort`. A IDE conecta em
  `<host>:<porta>`, e o runner é o lado que escuta.
- Em (b), basta `AndroidUtils.DeviceGetIpAddress()` devolver `127.0.0.1` no perfil local. **A verificar:** se o
  runner Android escuta em todas as interfaces ou só no IP do Wi-Fi, e se o `Debugger.dll` recebe essa linha
  pelo canal de saída do `CmdProcess` (seção 6, teste T6).
- Log: em (b), um `logcat -v brief --pid=<pid do :game>` (ou com filtros `yoyo:V` e o `option_android_logcat`
  do projeto) aberto pelo `:build` dentro do `AndroidRun_Command` substituído. As linhas vão para `Console`,
  portanto para o Output (`eOutputStream.Output | AssetCompiler`), como no PC.

---

## 5. Recomendação

Fazer em duas etapas pequenas, cada uma testável:

1. **Destravar o Build/Run** (P1): sem isso, nenhum comando da IDE (Run, Clean, Create Executable) chega ao Igor.
2. **Run local Android em `:game`** (P2 a P8): o F5 com `Test` passa a executar `-- Android Run` no perfil local.

Sobre licença: o plano não cria nenhuma verificação nova nem desvia alguma. Ele reaproveita o perfil local
Android VM que a exportação já usa (`IsLocalAndroidExport` no `GMAssetCompiler`,
`UseLocalKeystoreCredentials` no Igor). A rota sai do alvo `Test`, que a própria IDE isenta em
`CheckModuleForTargetPlatform`. Se houver dúvida de que "Test = Android local" é aceitável, quem decide é o
usuário antes da implementação.

---

## 6. Plano de implementação

### P1. Build.dll: `WinTrust.VerifySecurity` → `BuildBridge.VerifyBuildTools`

- Arquivo: `host/BuildPatches.cs`.
- Alvo: `YoYoStudio.Plugins.Build.WinTrust::VerifySecurity(System.String, System.String[]) : System.Boolean`
  (static). Substituir o corpo por `ldarg.0; ldarg.1; call GmIde.BuildBridge::VerifyBuildTools(string, string[]); ret`.
  `VerifyBuildTools` já aceita o stub e os links `files/bin/Igor`/`GMWebServer` e confere o `Igor.exe` com o
  manifesto (`RuntimeIntegrity`).
- O `TryPatch` sempre parte do `.original` e guarda um id só, então os patches do Build.dll precisam rodar num
  único `TryPatch` com id composto, por exemplo `android-apk-target-v4+wintrust-v1+local-run-v1`, como o IDE.dll
  já faz (nota da sessão unsafe-location).
- Atualizar o comentário de `BuildTarget.cs` sobre `TestPlatform_Actual`.

### P2. Build.dll: rotear `Test` para Android local

- `YoYoStudio.Plugins.Build.RunProgram::RunLinux(System.String) : System.Void` (internal): prefixo
  `if (GmIde.LocalRun.IsAndroidDevice()) { this.RunAndroid(runtime); return; }` (`RunAndroid(string)` é internal
  na mesma classe; `ldarg.0; ldarg.1; call instance RunAndroid`).
- `YoYoStudio.Plugins.Build.Common::CheckPlatformSettings(System.String, System.Boolean, System.String) : System.Boolean`
  (internal static): prefixo que, no aparelho, para `text` igual a `"linux"` ou `"android"` (depois da troca de
  `Test`), chama `GmIde.LocalRun.CheckSettings()` e devolve o resultado. Esse método confere: runtime VM (YYC →
  mensagem como no `ApkBridge`), `GMAndroidOptions` presente e modelo do runner disponível.
  `CheckSpineLicence` e `CheckModuleForTargetPlatform` ficam como estão.
- `RunProgram::RunAndroid` faz `Preferences.WriteAndroidPreferences()` e `Common.WriteAndroidTargetOptions(runtime)`
  (este só lê `DeviceManager.GetPlatformDeviceManager("android").Save()` e grava `TargetOptions`, com o
  dispositivo `Default` → dicionário vazio + `runtime`) e então `WriteToBuildFile(…, "Android Run", RunComplete)`.
  Com isso, Output, barra de progresso, Stop (`Common.BuildProcess`) e `RunComplete` funcionam sem código novo.
- Também rotear `StopProgram` (`Linux Stop` → `Android Stop`) da mesma forma, se o Stop do `CmdProcess` não bastar
  (ver P6).

### P3. `host/BuildBridge.cs`: perfil local por comando

- Em `Dispatch`, quando argv tiver `-- Android Run` ou `-- Android Stop`, definir no `:build`
  `GMIDE_LOCAL_ANDROID_EXPORT=1` e `GMIDE_LOCAL_ANDROID_RUN=1`. Hoje o primeiro só é definido pelo ambiente do
  stub da exportação; o `CmdProcess` da IDE não o tem.
- `ApplyEnvironment`: zerar `GMIDE_LOCAL_ANDROID_RUN` a cada pedido, como já faz com os outros três. Variáveis
  `GM*` herdadas ficariam no processo `:build` entre builds.
- `TargetHint`: avisar no Output quando o comando for `Linux Run` (não deve mais ocorrer depois do P2).

### P4. `host/IgorPatches.cs` (`PatchAndroid`): Run local

Nomes confirmados em `Igor.dll` `0c004a71…`. Trocar o marcador para `.gmide-igor-patch-6`.

| Método | Hoje | Novo |
|---|---|---|
| `Igor.AndroidUtils::IsAndroidDeviceConnected() : bool` | `false` | `IgorPatches.IsLocalAndroidRun()` (`true` só com `GMIDE_LOCAL_ANDROID_RUN=1`) |
| `Igor.AndroidUtils::DeviceGetIpAddress() : string` | original (adb) | prefixo: no Run local devolve `"127.0.0.1"` |
| `Igor.AndroidUtils::InstallAPKOnDevice(string,string,string,bool)` | no-op | mantém |
| `Igor.AndroidBuilder::AndroidRun_Command() : void` (public) | `adb logcat`/`am start` | corpo → `GmIde.LocalGameRun.RunAndWait(string zip, bool debug)`; código ≠ 0 → `Igor.Program::Exit(int)` |
| `Igor.AndroidBuilder::Stop() : void` | `adb am force-stop` | `GmIde.LocalGameRun.Stop()` + `Console.WriteLine("Igor complete.")` |
| `Igor.AndroidUtils::GetKeystorePassword/GetKeystoreAliasPassword(string)` | `ReadLocalKeystorePassword` (exige arquivos da exportação) | no Run local devolver `""` (não há assinatura) |

`Deploy` monta o caminho do "APK" (`<project_cache>/<projeto>_arm64-v8a_v<versão>.apk`) e chama
`CreateAPK` → `RunGradle`. O zip fica nesse caminho e o `AndroidRun_Command` o recebe por um campo estático
gravado em P5. No `IsRerun`, o Igor pula o `Deploy`: guardar o último zip por projeto num arquivo, por exemplo
`files/game-run/last.json`.

### P5. `host/ApkExport/Android/ApkBridge.cs`: zip em vez de APK no Run local

Em `RunGradle`, com `GMIDE_LOCAL_ANDROID_RUN=1`: localizar o módulo (`FindModule`), compactar
`src/main/assets/**` como `assets/…` (o mesmo laço do `ApkBuilder`, sem manifesto, recursos, assinatura nem
alinhamento) no caminho de saída que o Gradle usaria, conferir `assets/game.droid`, devolver `0`. Sem Gradle e
sem chave.

### P6. Novo `host/LocalGameRun.cs` (processo `:build`)

- `RunAndWait(zip, debug)`:
  1. Copiar o zip para `files/game-run/<hash do projeto>/game.zip` (fora do cache do Igor).
  2. `StartActivity(new Intent().SetClassName(pkg, "io.gmide.yyrunner.RunnerActivity").PutExtra("gmide.game_zip", …).PutExtra("gmide.save_dir", …).AddFlags(NewTask | ClearTask))`.
  3. Esperar o processo `io.gmide.android:game` aparecer (`ActivityManager.GetRunningAppProcesses`, mesmo UID).
  4. Ler `logcat -v brief --pid=<pid>` e mandar cada linha para `Console.WriteLine`.
  5. Voltar quando o PID sumir.
- `Stop()`: `Process.KillProcess(pid)` do `:game`.
- No `BuildService`, `IgorClientAborted` (Stop da IDE mata o stub) chama `LocalGameRun.Stop()` antes do
  `KillSelf()`; senão o jogo continua aberto.

### P7. Runner dentro do APK da IDE

- `tools/apk-template/make-template.py` (ou um novo `make-runner-aar.py`):
  - gerar o módulo do runner também como **biblioteca** Android (namespace `io.gmide.yyrunner`) e exportar
    `gmide-runner.aar`;
  - aplicar duas mudanças no fonte Java antes de compilar, mantendo o comportamento original sem os extras:
    - `DemoRenderer.onSurfaceCreated`: se a Intent da `RunnerActivity.CurrentActivity` tiver
      `gmide.game_zip`/`gmide.save_dir`, usar esses valores em `m_apkFilePath`/`m_saveFilesDir`;
    - nada no `RunnerJNILib`.
- `host/GmIde.Android.csproj`: `<AndroidLibrary Include="…\gmide-runner.aar" Bind="false" />` e as dependências
  androidx equivalentes (NuGet `Xamarin.AndroidX.AppCompat`/`Activity`, versões compatíveis com 1.7.0/1.9.3),
  `libyoyo.so` como `AndroidNativeLibrary` arm64 (com `extractNativeLibs`, como o `libgmexec.so`). Conferir que o
  `ILLink.Descriptors.xml`/trimming não afeta classes Java.
- `host/AndroidManifest.xml`:
  `<activity android:name="io.gmide.yyrunner.RunnerActivity" android:process=":game" android:exported="false" android:taskAffinity="io.gmide.android.game" android:theme="@style/Theme.AppCompat.NoActionBar" android:configChanges="…" android:screenOrientation="sensorLandscape"/>`
  e o `meta-data` `IsBuiltAsYoYoRunner=Yes`, que o runner lê do `<application>` (`ai.metaData`); verificar
  conflito com os meta-data da IDE.
- `host/MainApplication.cs`: detectar o processo `:game` (`Application.ProcessName` ou `/proc/self/cmdline`),
  chamar `com.yoyogames.runner.RunnerJNILib.Init()` (o que o `RunnerApplication.onCreate` faria) e não iniciar
  nada da IDE (SDL, Launcher, `BuildIntegration`).
- `GameRunnerActivity` (GX) continua como está.

### P8. Documentação

`docs/run-preview.md` com o fluxo final; atualizar os comentários de `BuildIntegration.cs`/`BuildTarget.cs` que
citam `docs/build-pipeline.md` (o arquivo não existe em `docs/`).

---

## 7. Riscos

- **Recursos e dex do runner no APK da IDE:** conflitos de versão do androidx com o que o .NET Android já traz;
  `R` de `io.gmide.yyrunner` gerado pelo aapt2 da IDE (com IDs diferentes do modelo, mas o código compilado
  contra o AAR usa o `R` gerado no build final; conferir que o AAR não leva constantes inline).
- **Mono no `:game`:** `MainApplication` e o `MonoRuntimeProvider` sobem em todo processo; medir memória e
  tempo de partida. Não carregar SDL no `:game`.
- **`libyoyo` lendo um zip que não é APK:** só a string `assets/game.droid` foi vista. Não está verificado se ele
  também lê `res/`, `lib/` ou o manifesto do zip (splash, ícone), nem se exige alinhamento ou descompressão
  específica. Testar zip `STORED` e `DEFLATED` (T3).
- **Pasta de saves:** sem o patch Java, `game_save_id`/`working_directory` caem em `files/` da IDE. Usar
  `files/game-run/<projeto>/`.
- **Orientação:** fixa no manifesto da IDE; jogos em retrato podem abrir de lado até o runner aplicar
  `RestrictOrientation`.
- **Extensões Java:** não entram (mesmo limite da exportação). `.so` de extensões precisariam ir para uma pasta
  carregável, também não verificado.
- **Exceção atual do WinTrust:** enquanto o P1 não entrar, um F5 pode deixar a fila de arquivos da IDE parada.
  Evitar F5 até lá.
- **Licença:** a rota `Test → Android` usa o perfil local existente; não altera as verificações de login dos
  módulos. Decisão do usuário (seção 5).
- **Igor muda entre runtimes:** os patches procuram métodos por nome e assinatura; falhar alto se não acharem
  (como `PatchAudioTools`).

## 8. Testes

- T1 (P1, PC): `tools/build-test/stub-test` já cobre `VerifyBuildTools`. Acrescentar um teste com Cecil no
  `Build.dll` real: corpo de `VerifySecurity` trocado, `ImplMap` intacto, patch reaplicável a partir do `.original`.
- T2 (P1, aparelho): F5 com `Test` antes do P2 deve mostrar no Output a saída do Igor `-- Linux Run` e falhar no
  `/bin/bash`, o que prova que o stub foi chamado. `GmIde.Igor` aparece no logcat.
- T3 (P5/P7, aparelho): abrir o `RunnerActivity` em `:game` com um zip montado à mão a partir do
  `src/main/assets` do Idle Game Template; conferir `yoyo: APK File Path ::` com o caminho do zip e a primeira sala.
- T4 (P2–P6): F5 → Output com compilação, linhas `yoyo` do jogo, `SUCCESS: Run Program Complete` ao fechar o jogo.
- T5: Stop durante o jogo fecha o `:game` e devolve a IDE ao estado `IDLE`; F5 seguido (rerun) não recompila.
- T6: F6 → o Output mostra `[Debugger]target=127.0.0.1,…`; a janela do debugger conecta; breakpoint para.
- T7: erro de GML → `FAILED: Run Program Complete` com o erro no Output e `:build` religado
  (`OnServiceDisconnected`).
- T8: regressão da exportação de APK (`apk-options-test`, `keystore-password-test`) e de um Run Windows simulado
  no PC (`bridge-harness`).

## 9. Quadro de evidências

| Afirmação | Tipo | Evidência |
|---|---|---|
| `VerifySecurity` → `VerifyEmbeddedSignature` → `WinVerifyTrust` (P/Invoke único) | estática, confirmada | REA `ev_41fa9145…`, `ev_53073100…`, `ev_0cd0d0e7…`; ilspy `WinTrust.cs` |
| `WriteToBuildFile` chama `VerifySecurity` antes do `CmdProcess` | estática, confirmada | ilspy `Common.cs:788-850` |
| Nenhum patch atual cobre `WinTrust` no Build.dll | código-fonte + log | `BuildPatches.cs`; log `Patch android-apk-target-v4: ja aplicado` |
| F5 hoje para no ícone Linux ausente (`CheckLinuxPlatformSettings`) | **observado** | logcat 22:48–22:50 (15× `File exception …options/linux/icons/64.png`, sem `GmIde.Igor`); `ls BaseProject/options`; `options_linux.yy` do projeto |
| Depois do ícone: exceção ou "Corrupt or damaged…" no WinTrust | inferência | não alcançado no aparelho |
| Comportamento da fila do FileSystem com a exceção | inferência | ilspy `AsyncQueue.DispatchCompletedRequests`, `FileRequest.ResultOperation` |
| Runtime sem módulo `linux`; `LocalSSH` usa `/bin/bash` | confirmado | `receipt.json` e `ls` no aparelho; ilspy `LocalSSH.cs` |
| `Deploy` sai com `-1` por `IsAndroidDeviceConnected` | confirmado | ilspy `AndroidBuilder.cs:81-102` + `IgorPatches.PatchAndroid` |
| Runner lê `assets/game.droid` do zip em `Startup(_apkPath…)` | estática; leitura do zip não observada | `DemoRenderer.java`, strings e exports do `libyoyo.so` (REA `ev_fa8e84f1…`) |
| Debugger usa `[Debugger]target=…,yydebug=…` | estática | ilspy `Utils.LaunchIDEDebugger`; strings do `Debugger.dll` |
| Mesmo UID lê o logcat do `:game` | conhecimento da plataforma | a testar em T4 |
