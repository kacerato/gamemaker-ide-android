# tools/devtest — roteiro de testes da IDE no aparelho (adb)

Scripts em bash para o Git Bash. Eles percorrem os fluxos principais da IDE no Android e guardam prints e trechos
de logcat por passo. Servem para confirmar ou derrubar as linhas da [matriz de funcionalidades](../../docs/feature-matrix.md).

**Estes scripts nunca foram rodados contra o aparelho.** Só o modo `--dry-run` foi testado no PC. As coordenadas
do `coords.conf` são estimativas tiradas do `logs/run14.png` e precisam de calibração.

## Arquivos

| Arquivo | Papel |
|---|---|
| `devtest.sh` | roteiro principal (passos 00–09) |
| `lib.sh` | funções: adb sem conversão de caminho do MSYS, toque, atalhos (`input keycombination`), texto, prints, logcat por passo, resumo |
| `coords.conf` | coordenadas (2772x1280, escala 43%) e tempos de espera. Itens `CALIBRAR` são chute |
| `screen.py` | imagem no PC (Pillow + numpy): `diff` entre prints, `locate` de template, `rows` (linhas de um menu/popup que abriu), `crop` de templates |
| `templates/` | ícones da toolbar recortados do `run14.png` (gerado por `screen.py crop`; refeito se a pasta sumir) |
| `probe.sh` | calibração: toca um ponto ou envia um atalho, tira print antes/depois e lista as linhas do menu que abriu |

## Passos

| # | Passo | Como | Verificação automática |
|---|---|---|---|
| 00 | preflight | `wm size`, SDK, suporte a `input keycombination`, lê o `.yyp` do aparelho | tamanho da tela = coords.conf |
| 01 | abrir projeto | `am force-stop` + `am start --es project <yyp>` | log `Project Loaded` |
| 02 | Asset Browser / Inspector | Ctrl+Shift+B, Ctrl+Shift+I | tela mudou |
| 03 | abrir cada tipo de recurso | um asset de cada pasta do `.yyp` (sprites, sounds, scripts, shaders, fonts, objects, rooms, sequences, animcurves, particles, notes…), aberto por **Ctrl+T (GoTo)** + nome + Enter, fechado com Ctrl+W; depois "Game Options" e "Included Files" pelo GoTo | exceções no log, tela mudou, app vivo |
| 04 | criar sprite | RightAlt+S, nome `devtest_spr_<hora>`, Enter, Ctrl+S | existe `sprites/<nome>/<nome>.yy` no aparelho |
| 05 | criar objeto | RightAlt+O, idem | existe `objects/<nome>/<nome>.yy` |
| 06 | script: editar e salvar | RightAlt+C, nome, Ctrl+End, Enter, digita `// devtest_marker_<hora>`, Ctrl+S | o `.gml` no aparelho contém o marcador |
| 07 | Preferences | Ctrl+Shift+P (se a tela não mudar, toca no botão da toolbar), Esc | tela mudou |
| 08 | Build > Run | toca no seletor de alvo, escolhe Windows/VM ou GX (coords `CALIBRAR`), F5, espera o log | `Igor complete.` / `Program Complete` / `FAILED` / `Permission Error`; prints a cada 30 s; `GameRunnerActivity` aberta? |
| 09 | fechar projeto | menu File + item `FILE_CLOSE_PROJECT` (`CALIBRAR`) | prints; `file-menu-rows.txt` ajuda a achar o item |

Status de cada passo no `summary.md`: **OK**, **WARN** (exceção no log ou tela parada), **FAIL** (verificação falhou),
**TIMEOUT**, **CRASH** (o app morreu; o passo seguinte reabre o projeto) e **SKIP**.

Os passos 04–06 **alteram o projeto** (criam assets e salvam). O padrão é a cópia em
`/storage/emulated/0/GameMakerProjects/Idle Game Template/`. Use `--no-create` para pular esses passos.

## Uso

```bash
export ANDROID_SERIAL=adb-53e1eb7-cDkkst._adb-tls-connect._tcp
```
```bash
bash tools/devtest/devtest.sh
```
```bash
bash tools/devtest/devtest.sh -s 00,01,03
```
```bash
bash tools/devtest/devtest.sh --target both --no-create
```
```bash
bash tools/devtest/devtest.sh --dry-run --yyp "/c/Users/donod/GameMakerProjects/Idle Game Template/Idle Game Template.yyp"
```

Opções: `-p <yyp no aparelho>`, `-s <passos>`, `--target vm|gx|both`, `--no-create`, `--no-fresh` (não fecha o app
antes do passo 01), `--yyp <cópia local do .yyp>`, `-o <pasta>`, `--dry-run`, `--list`.
`ADB=<caminho>` troca o adb (padrão: `$LOCALAPPDATA/Android/Sdk/platform-tools/adb.exe`, o mesmo do `deploy.sh`).

Saída em `logs/devtest/<AAAAMMDD-HHMMSS>/`:
`summary.md`, `run.txt` (o que o script fez), `device.txt`, `project.yyp`, e por passo `NN-nome-*.png`, `NN-nome.log`
(logcat filtrado por `GmIde|DOTNET|SDL|monodroid|yyal|AndroidRuntime|FATAL…`), `NN-nome.errors.txt`,
`NN-nome.build.log`, `NN-nome.crash.log` e `*-rows.txt`.

## Calibração (para a sessão principal)

1. Rode `bash tools/devtest/devtest.sh -s 00,01,02` e confira os prints. Se a escala da UI mudou, todas as
   coordenadas mudam na mesma proporção.
2. Para cada item `CALIBRAR` do `coords.conf`, use o `probe.sh`. Exemplo: `bash tools/devtest/probe.sh MENU_FILE`
   mostra as linhas do menu File (`índice y_centro x0 x1`); copie `"x y_centro"` do item "Close Project" para
   `FILE_CLOSE_PROJECT`. O mesmo vale para o popup de alvo (`TB_TARGET_ICON`).
3. Os ícones da toolbar (`TB_RUN`, `TB_STOP`, `TB_PREFS`…) são achados por template perto da posição esperada
   (`TPL_SEARCH_RADIUS`). Se o tema ou a escala mudar, apague `templates/` e ajuste as linhas `TPL_` para um print novo
   (`python -I tools/devtest/screen.py crop <print.png> tools/devtest/coords.conf tools/devtest/templates`).

## Por que atalhos em vez de toques

O roteiro prefere atalhos da IDE (lista em `docs/feature-matrix.md`, seção 1.3) porque não dependem do layout:
Ctrl+T abre qualquer asset pelo nome, RightAlt+S/O/C cria sprite/objeto/script, Ctrl+Shift+B/I/P abre
painéis, Ctrl+S salva, F5 roda. O envio usa `adb shell input keycombination` (Android 13+). Ele aperta os
modificadores antes da letra, então o SDL atualiza o estado de Ctrl/Alt/Shift. Se o aparelho não tiver
`keycombination`, o preflight avisa e os atalhos com modificador não são enviados.

Limites conhecidos:

- Digitar (`input text`) só funciona se a IDE tiver ligado a entrada de texto do SDL no campo focado
  (P0-1 da matriz). Se o passo 03 mostrar o GoTo aberto e vazio no print `*-typed.png`, é esse o problema.
- O RightAlt dos atalhos "Create …" é `KEYCODE_ALT_RIGHT`. Se a IDE não reconhecer, o passo 04 falha e o print
  `*-created.png` mostra a tela igual.
- O seletor de alvo lançou exceção no run14 (`TargetSelect_UpdatePopUp`). O passo 08 registra o popup e segue com F5
  no alvo atual mesmo que a escolha não funcione.
