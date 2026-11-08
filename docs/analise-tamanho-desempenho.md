# Tamanho, desempenho e economia de energia

Análise feita em 08/11/2026 sobre o APK atual (`host/bin/Release/.../io.gmide.android-Signed.apk`) e o código da IDE descompilado. Ainda não muda nada no projeto. Os números de tamanho foram medidos; os de desempenho são estimativas até a medição no aparelho (seção 4).

## 1. Resumo

| | Hoje | Corte seguro (A+B) | Corte completo (A+B+C) |
|---|---|---|---|
| APK | 498 MB | ~265 MB | ~145 MB |
| Pacote embutido (zip) | 447 MB | ~212 MB | ~95 MB |
| IDE + runtime extraídos | 840 MB | ~405 MB | ~180 MB |
| **Total no aparelho** (APK + extraídos) | **~1,34 GB** | **~670 MB** | **~325 MB** |

Quase metade do espaço vai para arquivos de Windows que o Android nunca executa. Cortá-los não tira nenhuma função da IDE.

Em desempenho, o maior ganho vem de três frentes:
- não girar o loop da IDE a 60 vezes por segundo quando nada acontece;
- deixar a tela do aparelho a 60 Hz enquanto a IDE estiver aberta;
- compilar antecipadamente (AOT) o código .NET.

Vulkan tem custo alto e ganho provavelmente pequeno para uma IDE 2D que só redesenha quando algo muda. A recomendação é medir antes (seção 3.5).

## 2. Tamanho

### 2.1 O que tem dentro

APK de 498 MB:
- **447 MB:** `assets/bootstrap/gmide-package.zip`, com a instalação inteira do GameMaker para Windows, o runtime e os Prefabs.
- **38 MB:** bibliotecas nativas arm64 (libyoyo 14,5; Magick 6,7; SDL2 5,9; freetype 3,9; ffmpeg+ffprobe 5,8; ...).
- **15 MB:** `runtime-modules/android.zip`.
- **6,8 MB:** `classes.dex`.
- **~8 MB:** BCL do Mono.

O zip descompactado (840 MB), classificado pelo cabeçalho PE de cada arquivo (`classify.py`, `zipcut.py`):

| Nível | O que é | No zip | Extraído | Risco de tirar |
|---|---|---|---|---|
| **A** | DLL/EXE **nativos de Windows** (x64/x86/arm64): coreclr, clrjit, wpfgfx, D3DCompiler, Magick.Native-x64, libSkiaSharp.dll, Runner.exe, makensis, vcredist, putty, openssl.exe, ffmpeg.exe... e `.pdb` | 157 MB | 266 MB | **Nenhum**: o Android não carrega binário PE |
| **B** | Assemblies de **framework .NET** que o Mono do APK já tem (System.Private.CoreLib, System.Private.Xml, System.Linq.Expressions...), em `ide/` e nas pastas das ferramentas (asset compiler, Igor, webserver) | 77 MB | 169 MB | **Muito baixo** (ver abaixo) |
| **C** | Framework **só de Windows** (WPF, WinForms, System.Drawing.Common, Xaml, Printing), o `node-v24.zip` do gmpm e o runner Android para armeabi-v7a/x86_64 | 96 MB | 188 MB | **Precisa de prova em execução** |
| Manter | Assemblies da YoYo (IDE, plugins, GMAssetCompiler, Igor, ProjectTool), GUI/skins, fontes, idiomas, prefabs, GX wasm, BaseProject, runner arm64 | 95 MB | 179 MB | — |

**Por que B é seguro:** o Launcher e o `ToolLoadContext` (`BuildBridge.cs:704`) pedem primeiro ao contexto padrão, que resolve pelo BCL do APK. As cópias de Windows só seriam lidas se o Mono não tivesse o assembly.

**O que confirmar antes de tirar C:**
- **WPF/WinForms:** não funcionam no Android (dependem de nativos Windows), mas a IDE pode *referenciar* tipos deles em caminhos raros. O `ResolveFromIdeDir` carregaria a DLL e, sem ela, aquele caminho lançaria exceção. Para confirmar, registrar `AppDomain.AssemblyLoad` numa sessão completa (abrir projeto, todos os editores, F5, Gerar APK, Preferências, Marketplace) e verificar que nenhum desses nomes aparece.
- **node-v24.zip** é o Node de Windows do `gmpm` (gerenciador de pacotes). Ver se o gmpm é usado no port.
- **armeabi-v7a/x86_64 do runner:** só faltam se alguém marcar essas ABIs nas Game Options para exportar. Dá para restringir a ABI à arm64 no "Gerar APK".

### 2.2 Como cortar

- **Onde filtrar:** em `tools/make-package.sh`, com uma lista gerada pelo classificador (cabeçalho PE + nome no BCL), não à mão. O `package.json` já é montado só com o que entra no zip.
- **Integridade:** a verificação no aparelho (`RuntimeIntegrity`, `IntegrityCheck`) confere hash arquivo a arquivo do que existe e das DLLs que a IDE lista. Essas são da YoYo e ficam.
- **Testes depois do corte:**
  - instalação limpa;
  - abrir projeto;
  - compilar (Igor e asset compiler em processo);
  - F5;
  - Gerar APK;
  - prévia de som (ffmpeg);
  - importar imagem (Magick).

### 2.3 Outros ganhos menores

| Item | Ganho | Como |
|---|---|---|
| Strip das `.so` (SDL2 5,9→1,4 MB; freetype 3,9→0,7; yoga 2,2→0,4; FNA3D 1,9→0,4; onig 1,7→0,6) | **~11 MB** no APK | `llvm-strip --strip-unneeded` no fim de `native/build-native.sh` |
| `classes.dex` 6,8 MB (AndroidX/AppCompat dos AARs do runner) | 2–4 MB | R8 com regras `-keep` para o runner (tem reflexão: testar F5) |
| Zip guardado no APK **e** extraído (o APK não pode ser reduzido depois de instalado) | até o tamanho do zip | Com o corte, o zip cai para ~95–212 MB e o problema quase some. Alternativa: zip separado na pasta Download, importado e apagado (o `PackageImport` já suporta) |

## 3. Desempenho e energia

### 3.1 Loop da IDE parado (maior ganho de bateria, esforço baixo)

- **O que o loop faz:** `IDE.Run` processa entrada, timers e layout a **60 Hz sempre** (`WaitForTick` com `GetCurrentFPS()`). Só *desenha* quando algo muda (`RenderFrameRequired`), mas acorda a CPU 60 vezes por segundo mesmo parada.
- **O que já existe e nunca liga aqui:** a IDE tem modo economia (`Power Saving`: AC 100%, bateria e fora de foco), mas `Laptop.IsRunningOnBattery` devolve sempre `false` no Linux, então no Android nunca entra.

Proposta:
1. Patch em `Laptop.IsRunningOnBattery` usando `BatteryManager` do Android. As preferências da própria IDE valem (`machine.General Settings.Power Saving.BatteryPower`).
2. **Modo ocioso:** sem toque/tecla por ~2 s e sem animação, o loop cai para ~15 Hz. O primeiro toque volta a 60 na hora, pelo prefixo em `GetCurrentFPS`.
3. Activity em segundo plano: confirmar que o desktop perde foco e aplica `NotActiveFPS` (10 Hz).

**Ganho esperado:** de 60 para 15 acordadas por segundo com a IDE parada na tela. Nada muda durante o uso.

### 3.2 Tela a 60 Hz enquanto a IDE estiver aberta (esforço baixo)

O REDMAGIC tem tela de 120/144 Hz, mas a IDE nunca passa de 60 quadros. Basta pedir 60 Hz para a superfície com `Surface.setFrameRate(60, FRAME_RATE_COMPATIBILITY_FIXED_SOURCE)` (API 30+, no `GmIdeActivity`). Painéis de taxa variável economizam bem com isso. O jogo (`:game`) continua livre para usar a taxa alta.

### 3.3 Código .NET compilado antes (AOT): o maior ganho de fluidez

- **Situação hoje:** `RunAOTCompilation=false`, `AndroidEnableProfiledAot=false`. Todo o código da IDE (IDE.dll com 3 MB de IL, plugins, BCL) passa pelo JIT do Mono na primeira vez que roda.
- **Efeito:** abertura lenta e engasgos ao abrir cada editor pela primeira vez, com CPU (e bateria) gasta compilando.

| Passo | Esforço | Ganho |
|---|---|---|
| `AndroidEnableProfiledAot=true` (BCL pré-compilada) | 1 linha | Abertura mais rápida e menos JIT em XML/JSON/LINQ |
| AOT dos assemblies da IDE | Alto | Grande: abertura, primeiro uso de cada editor, CPU |
| Ajuste do GC (`MONO_GC_PARAMS=nursery-size=...`) | Baixo, medir | Menos pausas: a GUI aloca muito por quadro |

**O que bloqueia o AOT da IDE:** hoje os patches de IL são aplicados **no aparelho** (`IdePatches`), e as DLLs são carregadas de `files/ide`. Um AOT pré-compilado precisa dos mesmos bytes. O caminho limpo:
1. aplicar os patches no PC, durante o build (o mesmo código Cecil, fora do app);
2. compilar AOT os assemblies já corrigidos;
3. empacotar as imagens AOT junto.

De quebra, some o tempo de patch na primeira abertura.

### 3.4 Resolução de desenho (opcional, troca nitidez por energia)

A IDE desenha na resolução nativa (2772×1280). Uma opção "escala de renderização" (`SurfaceHolder.setFixedSize`, por exemplo 75%) corta ~44% dos pixels por quadro. O texto perde um pouco de nitidez, então isso seria uma preferência desligada por padrão, não um padrão.

### 3.5 Vulkan

- **Como desenha hoje:** IDE → `Graphics.dll` → **FNA3D (driver OpenGL ES 3)** → SDL2.
- **Por que Vulkan custa caro:** no FNA3D atual, Vulkan só existe pelo driver `SDL_GPU` (`FNA3D_Driver_SDL.c`), que **exige SDL3**. A IDE usa SDL2 (SDL2-CS), e o nosso `SDLActivity` (IME, barra de símbolos, toque) é o Java do SDL2. Seria preciso:
  1. trocar para SDL3 + `sdl2-compat` (a API SDL2 sobre SDL3);
  2. migrar o Java para o do SDL3 e reaplicar as mudanças do port;
  3. compilar o FNA3D com `BUILD_SDL3` e o SDL_shadercross (shaders MojoShader → SPIR-V);
  4. revalidar teclado, toque, PiP e preview.
- **Por que o ganho é incerto:** Vulkan corta o custo de *driver* por chamada de desenho. A IDE é uma GUI 2D com lotes de quadriláteros (`VertexManager`), e só desenha quando algo muda. O gargalo mais provável é CPU no lado .NET (JIT, layout, medição de texto), não o driver GL.
- **ANGLE** (GLES sobre Vulkan) não traz ganho; só adiciona uma camada.

**Recomendação:** medir antes (seção 4). Se o tempo de GPU/driver por quadro for relevante durante rolagem ou zoom no editor de sala, aí vale o projeto SDL3/SDL_GPU, como etapa própria.

Ideias "de Vulkan" que dá para usar sem trocar de API:
- desenhar só sob demanda (já existe);
- atualização parcial da tela (`EGL_KHR_partial_update` / `swap_buffers_with_damage`). O FNA3D não expõe isso, então seria um patch no FNA3D, para redesenhar só o retângulo sujo (por exemplo, o cursor piscando no editor de código).

## 4. Plano de medição (quando o adb voltar)

| Medida | Comando | Para quê |
|---|---|---|
| CPU parada / rolando | `adb shell top -H -p $(pidof io.gmide.android) -d 1` | Ganho do modo ocioso (3.1) |
| Quadros e jank | `adb shell dumpsys gfxinfo io.gmide.android framestats` | Tempo por quadro na rolagem e zoom |
| Onde a CPU vai | `simpleperf record -p <pid> --call-graph dwarf` (NDK) | JIT x GC x GL x layout: decide entre AOT e Vulkan |
| GPU / driver | Perfetto (`gpu.renderstages`) ou Android GPU Inspector | Decide o Vulkan (3.5) |
| Energia | `dumpsys batterystats --reset` + 10 min parado + `dumpsys batterystats io.gmide.android` | Antes e depois de 3.1/3.2 |
| Abertura | `adb logcat` do boot até "IDE pronta" | Antes e depois do AOT (3.3) |

## 5. Ordem sugerida

| # | Item | Esforço | Ganho | Risco |
|---|---|---|---|---|
| 1 | Corte A+B do pacote | Baixo | −230 MB no APK, −435 MB extraídos | Muito baixo |
| 2 | Strip das `.so` | Muito baixo | −11 MB | Nenhum |
| 3 | Modo ocioso + bateria + tela a 60 Hz | Baixo | Bateria com a IDE parada | Baixo |
| 4 | `AndroidEnableProfiledAot` + ajuste do GC | Baixo | Abertura e pausas | Baixo |
| 5 | Registro de assemblies carregados → corte C | Médio | −120 MB no APK | Médio (exige prova) |
| 6 | Patches no PC + AOT da IDE | Alto | Fluidez e abertura | Médio |
| 7 | R8 no dex | Médio | −2 a 4 MB | Médio |
| 8 | SDL3 + SDL_GPU (Vulkan) | Muito alto | Incerto, medir antes | Alto |

Nenhum item remove funções da IDE: os cortes A e B tiram só o que o Android não pode executar ou já tem no próprio runtime. O C só sai com prova de que não é carregado.
