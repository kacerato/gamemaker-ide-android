#!/usr/bin/env bash
# Roteiro de testes da IDE GameMaker no Android, via adb (Git Bash no Windows).
#
# Percorre os fluxos principais e salva, por passo, prints e o trecho de logcat em
# logs/devtest/<data>/. No fim escreve logs/devtest/<data>/summary.md.
#
#   bash tools/devtest/devtest.sh                 # todos os passos
#   bash tools/devtest/devtest.sh --list          # lista os passos
#   bash tools/devtest/devtest.sh -s 01,03,06     # so alguns passos (o 01 abre o projeto)
#   bash tools/devtest/devtest.sh --dry-run       # nao toca no aparelho; grava os comandos em commands.txt
#   bash tools/devtest/devtest.sh --target gx     # alvo do Build>Run: vm (padrao), gx ou both
#   bash tools/devtest/devtest.sh --no-create     # pula os passos que criam assets no projeto
#   bash tools/devtest/devtest.sh --no-fresh      # nao fecha o app antes do passo 01
#   bash tools/devtest/devtest.sh --yyp ARQ.yyp   # le a lista de assets de uma copia local do .yyp
#
# ATENCAO: os passos 04-06 criam assets (devtest_spr_*, devtest_obj_*, devtest_scr_*) e salvam o
# projeto. Use uma COPIA do projeto (o padrao ja e a copia em /storage/emulated/0/GameMakerProjects).
#
# Coordenadas: tools/devtest/coords.conf (estimadas do logs/run14.png; calibrar).
# Requisitos: adb (padrao: $LOCALAPPDATA/Android/Sdk/platform-tools/adb.exe; ou ADB=...),
#             ANDROID_SERIAL se houver mais de um aparelho, python com Pillow+numpy (opcional).
set -u

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"

PROJECT="/storage/emulated/0/GameMakerProjects/Idle Game Template/Idle Game Template.yyp"
STEPS=""
DRY=0
TARGETS="vm"
CREATE=1
FRESH=1
OUT=""
LOCAL_YYP=""

usage() { sed -n '2,23p' "$0" | sed 's/^# \{0,1\}//'; }

ALL_STEPS="00:preflight 01:open-project 02:asset-browser 03:open-resources 04:create-sprite 05:create-object 06:script-edit-save 07:preferences 08:build-run 09:close-project"

while [ $# -gt 0 ]; do
  case "$1" in
    -p|--project) PROJECT="$2"; shift ;;
    -s|--steps)   STEPS="$2"; shift ;;
    --dry-run)    DRY=1 ;;
    --target)     TARGETS="$2"; shift ;;
    --no-create)  CREATE=0 ;;
    --no-fresh)   FRESH=0 ;;
    -o|--out)     OUT="$2"; shift ;;
    --yyp)        LOCAL_YYP="$2"; shift ;;
    --list)       for s in $ALL_STEPS; do echo "${s%%:*}  ${s#*:}"; done; exit 0 ;;
    -h|--help)    usage; exit 0 ;;
    *) echo "opcao desconhecida: $1"; usage; exit 1 ;;
  esac
  shift
done
[ "$TARGETS" = both ] && TARGETS="vm gx"

TS=$(date +%Y%m%d-%H%M%S)
OUT="${OUT:-$ROOT/logs/devtest/$TS}"
mkdir -p "$OUT"
export OUT DRY HERE PROJECT

# shellcheck source=coords.conf
source "$HERE/coords.conf"
# shellcheck source=lib.sh
source "$HERE/lib.sh"

PROJDIR="${PROJECT%/*}"
SUFFIX="${TS//-/}"           # sufixo unico para os assets criados
SUFFIX="${SUFFIX:4}"         # MMDDHHMMSS

want() { [ -z "$STEPS" ] && return 0; case ",$STEPS," in *",$1,"*) return 0 ;; esac; return 1; }

{
  echo "# devtest $TS"
  echo
  echo "- projeto: \`$PROJECT\`"
  echo "- aparelho: \`${ANDROID_SERIAL:-padrao}\`  dry-run: $DRY  alvos: $TARGETS"
  echo "- coords: \`tools/devtest/coords.conf\`"
  echo
  echo "Status: OK / WARN (erro no log ou tela parada) / FAIL / TIMEOUT / CRASH / SKIP. 'tela' = fracao de pixels"
  echo "que mudou entre o primeiro e o ultimo print do passo."
  echo
  echo "| passo | descricao | status | tela | erros | nota |"
  echo "|---|---|---|---|---|---|"
} > "$OUT/summary.md"

# Templates para o casamento de imagem (recortados do print de referencia, uma vez).
if [ ! -d "$HERE/templates" ] && [ -f "$REF_PNG" ]; then
  "$PY" -I "$HERE/screen.py" crop "$REF_PNG" "$HERE/coords.conf" "$HERE/templates" >/dev/null 2>&1 || true
fi

# ---------------------------------------------------------------------------------------------
step_preflight() {
  begin_step preflight "aparelho, tela e suporte a keycombination"
  if [ "$DRY" != 1 ]; then
    [ -x "$ADB" ] || [ -f "$ADB" ] || { say "adb nao encontrado em $ADB"; exit 1; }
    adb_ get-state >/dev/null 2>&1 || { say "nenhum aparelho no adb (defina ANDROID_SERIAL)"; exit 1; }
  fi
  {
    echo "wm size: $(adb_out shell wm size)"
    echo "wm density: $(adb_out shell wm density)"
    echo "sdk: $(adb_out shell getprop ro.build.version.sdk)  modelo: $(adb_out shell getprop ro.product.model)"
    echo "app pid: $(app_pid)"
  } | tee "$OUT/device.txt" >&2
  if [ "$DRY" != 1 ]; then
    if adb_out shell input 2>&1 | grep -q keycombination; then HAS_KEYCOMBO=1; else HAS_KEYCOMBO=0; note "sem 'input keycombination': atalhos com Ctrl/Alt nao serao enviados"; fi
    adb_out shell wm size | grep -q "${SCREEN_W}x${SCREEN_H}\|${SCREEN_H}x${SCREEN_W}" || note "tela diferente de ${SCREEN_W}x${SCREEN_H}: recalibrar coords.conf"
    [ -s "$OUT/project.yyp" ] || adb_out exec-out "cat '$PROJECT'" > "$OUT/project.yyp"
    [ -s "$OUT/project.yyp" ] || note "nao consegui ler o .yyp ($PROJECT)"
  fi
  end_step OK
}

step_open_project() {
  begin_step open-project "abrir o projeto pela intent e esperar 'Project Loaded'"
  if [ "$FRESH" = 1 ]; then adb_ shell am force-stop "$PKG"; sleep 2; fi
  STEP_T0=$(dev_time)
  adb_ shell am start -n "$ACTIVITY" --es project "'$PROJECT'"
  local st=OK
  wait_log "Project Loaded|OnProjectLoaded" "$WAIT_PROJECT" || st=TIMEOUT
  sleep 5
  shot loaded
  # tira o foco da Welcome Tab e registra o layout inicial
  wait_log "IDE Loaded--" 5 >/dev/null 2>&1 || true
  end_step "$st"
}

step_asset_browser() {
  begin_step asset-browser "abrir o Asset Browser (Ctrl+Shift+B) e o Inspector (Ctrl+Shift+I)"
  shot before
  combo KEYCODE_CTRL_LEFT KEYCODE_SHIFT_LEFT KEYCODE_B; sleep "$WAIT_SHORT"
  shot asset-browser
  combo KEYCODE_CTRL_LEFT KEYCODE_SHIFT_LEFT KEYCODE_I; sleep "$WAIT_SHORT"
  shot inspector
  end_step OK
}

# Escolhe um asset de cada tipo a partir do .yyp (lido no preflight).
list_resources() {
  [ -s "$OUT/project.yyp" ] || return 0
  "$PY" -I - "$OUT/project.yyp" <<'EOF'
import json, re, sys
t = open(sys.argv[1], encoding="utf-8-sig").read()
t = re.sub(r",(\s*[\]}])", r"\1", t)          # o GameMaker grava virgulas finais
d = json.loads(t)
seen = {}
for r in d.get("resources", []):
    rid = r.get("id", {})
    name, path = rid.get("name"), rid.get("path", "")
    kind = path.split("/", 1)[0]
    if name and kind and kind not in seen:
        seen[kind] = name
order = ["sprites", "tilesets", "sounds", "paths", "scripts", "shaders", "fonts", "timelines",
         "objects", "rooms", "sequences", "animcurves", "particles", "notes", "extensions"]
for k in order + sorted(set(seen) - set(order)):
    if k in seen:
        print(k, seen[k])
EOF
}

open_by_goto() { # open_by_goto TEXTO ROTULO: Ctrl+T, digita, Enter
  combo KEYCODE_CTRL_LEFT KEYCODE_T; sleep "$WAIT_SHORT"
  shot "$2-goto"
  type_text "$1"; sleep 1
  shot "$2-typed"
  key KEYCODE_ENTER; sleep "$WAIT_EDITOR"
  shot "$2-opened"
}

step_open_resources() {
  local list kind name
  list=$(list_resources)
  if [ -z "$list" ]; then
    begin_step open-resources "abrir cada tipo de recurso (sem .yyp: pulado)"
    note "lista de recursos vazia (dry-run ou .yyp ilegivel)"
    end_step SKIP
    return
  fi
  printf '%s\n' "$list" > "$OUT/resources-tested.txt"
  while read -r kind name; do
    [ -n "$kind" ] || continue
    ensure_running || true
    begin_step "open-$kind" "abrir $kind '$name' pelo GoTo (Ctrl+T)"
    shot before
    open_by_goto "$name" "$kind"
    combo KEYCODE_CTRL_LEFT KEYCODE_W; sleep 1   # fecha a aba do editor
    shot closed
    end_step OK
  done <<<"$list"
  # Itens sem asset proprio: tenta pelo GoTo tambem (podem nao existir no GoTo).
  local extra
  for extra in "Game Options" "Included Files"; do
    ensure_running || true
    begin_step "open-${extra// /-}" "abrir '$extra' pelo GoTo"
    shot before
    open_by_goto "$extra" "x"
    key KEYCODE_ESCAPE
    end_step OK
  done
}

create_asset() { # create_asset LETRA_KEYCODE PREFIXO PASTA
  local keyc="$1" name="$2" folder="$3" st=OK
  shot before
  # RightAlt+<letra> = Create <tipo> (GlobalHotkeys.json)
  combo KEYCODE_ALT_RIGHT "$keyc" || st=FAIL
  sleep "$WAIT_SHORT"
  shot created
  # O asset novo entra em modo renomear no Asset Browser [I]; digita o nome e confirma.
  type_text "$name"; key KEYCODE_ENTER; sleep "$WAIT_SHORT"
  shot renamed
  combo KEYCODE_CTRL_LEFT KEYCODE_S; sleep 3
  shot saved
  if [ "$DRY" != 1 ]; then
    if adb_out shell "ls '$PROJDIR/$folder/$name/$name.yy'" | grep -q "$name.yy"; then
      note "arquivo $folder/$name/$name.yy criado"
    else
      st=FAIL
      note "nao achei $folder/$name/$name.yy; mais novos: $(adb_out shell "ls -t '$PROJDIR/$folder' | head -3" | tr '\n' ' ')"
    fi
  fi
  end_step "$st"
}

step_create_sprite() {
  [ "$CREATE" = 1 ] || { begin_step create-sprite "criar sprite"; end_step SKIP; return; }
  ensure_running || true
  begin_step create-sprite "criar sprite (RightAlt+S), renomear e salvar"
  create_asset KEYCODE_S "devtest_spr_$SUFFIX" sprites
}

step_create_object() {
  [ "$CREATE" = 1 ] || { begin_step create-object "criar objeto"; end_step SKIP; return; }
  ensure_running || true
  begin_step create-object "criar objeto (RightAlt+O), renomear e salvar"
  create_asset KEYCODE_O "devtest_obj_$SUFFIX" objects
}

step_script_edit() {
  [ "$CREATE" = 1 ] || { begin_step script-edit-save "criar script e editar"; end_step SKIP; return; }
  ensure_running || true
  local name="devtest_scr_$SUFFIX" marker="devtest_marker_$SUFFIX" st=OK
  begin_step script-edit-save "criar script (RightAlt+C), digitar no editor e salvar (Ctrl+S)"
  shot before
  combo KEYCODE_ALT_RIGHT KEYCODE_C || st=FAIL
  sleep "$WAIT_SHORT"
  type_text "$name"; key KEYCODE_ENTER; sleep "$WAIT_EDITOR"
  shot editor
  # Vai para o fim do arquivo e acrescenta uma linha de comentario com o marcador.
  combo KEYCODE_CTRL_LEFT KEYCODE_MOVE_END
  key KEYCODE_ENTER
  type_text "// $marker"
  sleep 1
  shot typed
  combo KEYCODE_CTRL_LEFT KEYCODE_S; sleep 3
  shot saved
  if [ "$DRY" != 1 ]; then
    adb_out shell "cat '$PROJDIR/scripts/$name/$name.gml'" > "$OUT/${STEP_ID}.gml"
    if grep -q "$marker" "$OUT/${STEP_ID}.gml"; then
      note "marcador salvo em scripts/$name/$name.gml"
    else
      st=FAIL
      if [ -s "$OUT/${STEP_ID}.gml" ]; then note "script existe mas sem o marcador (digitacao nao chegou?)"
      else note "scripts/$name/$name.gml nao existe"; fi
    fi
  fi
  key KEYCODE_ESCAPE
  end_step "$st"
}

step_preferences() {
  ensure_running || true
  begin_step preferences "abrir Preferences (Ctrl+Shift+P; se nada mudar, botao da toolbar) e fechar"
  shot before
  combo KEYCODE_CTRL_LEFT KEYCODE_SHIFT_LEFT KEYCODE_P; sleep "$WAIT_EDITOR"
  shot shortcut
  if [ "$DRY" != 1 ]; then
    local d
    d=$("$PY" -I "$HERE/screen.py" diff "$OUT/${STEP_ID}-before.png" "$OUT/${STEP_ID}-shortcut.png" 2>/dev/null | awk '{print $1}')
    if [ "${d:-0}" = 0 ]; then
      note "atalho nao abriu; tentando o botao da toolbar"
      tap TB_PREFS; sleep "$WAIT_EDITOR"
      shot button
    fi
  fi
  key KEYCODE_ESCAPE; sleep 1
  shot closed
  end_step OK
}

choose_target() { # choose_target vm|gx
  shot target-before
  tap TB_TARGET_ICON; sleep "$WAIT_SHORT"
  shot target-popup
  [ "$DRY" != 1 ] && "$PY" -I "$HERE/screen.py" rows "$OUT/${STEP_ID}-target-before.png" "$OUT/${STEP_ID}-target-popup.png" \
      > "$OUT/${STEP_ID}-target-rows.txt" 2>/dev/null
  if [ "$1" = gx ]; then
    tap TARGET_PLATFORM_GX
  else
    tap TARGET_PLATFORM_WINDOWS; sleep 1
    tap TARGET_OUTPUT_VM
  fi
  sleep 1
  tap TARGET_DEVICE_FIRST; sleep 1
  shot target-chosen
  key KEYCODE_ESCAPE; sleep 1
}

step_build_run() {
  local tgt st
  for tgt in $TARGETS; do
    ensure_running || true
    st=OK
    begin_step "build-run-$tgt" "Build > Run (F5) com alvo $tgt; esperar fim do build"
    choose_target "$tgt"
    shot before-run
    key KEYCODE_F5
    local t=0 rc=1
    # prints periodicos enquanto compila
    while [ "$t" -lt "$WAIT_BUILD" ]; do
      if wait_log 'Igor complete\.|Program Complete|FAILED:|Permission Error|Corrupt or damaged|Build failed' 30; then rc=0; break; fi
      app_alive || { rc=2; break; }
      t=$((t + 30)); shot "building-${t}s"
      [ "$DRY" = 1 ] && { rc=0; break; }
    done
    [ "$rc" = 1 ] && st=TIMEOUT
    if [ -f "$OUT/${STEP_ID}.match" ] && grep -q -E 'FAILED|Permission Error|Corrupt|failed' "$OUT/${STEP_ID}.match"; then
      st=FAIL; note "build falhou: $(cut -c1-160 "$OUT/${STEP_ID}.match")"
    fi
    sleep 5
    shot after-run
    # O runner GX abre por cima (GameRunnerActivity): print e Voltar.
    if [ "$DRY" != 1 ] && adb_out shell dumpsys activity activities | grep -E "mResumedActivity|topResumedActivity" | grep -q GameRunner; then
      note "GameRunnerActivity aberta"
      sleep 5; shot runner
      key KEYCODE_BACK; sleep 2
    fi
    tap TB_STOP; sleep 2
    shot stopped
    step_log
    grep -E "GmIde\.Build|GmIde\.Runner|GmIde\.Tools|Igor|Program Complete" "$OUT/${STEP_ID}.log" > "$OUT/${STEP_ID}.build.log" 2>/dev/null || true
    end_step "$st"
  done
}

step_close_project() {
  ensure_running || true
  begin_step close-project "File > Close Project (coordenada a calibrar)"
  shot before
  tap MENU_FILE; sleep "$WAIT_SHORT"
  shot file-menu
  [ "$DRY" != 1 ] && "$PY" -I "$HERE/screen.py" rows "$OUT/${STEP_ID}-before.png" "$OUT/${STEP_ID}-file-menu.png" \
      > "$OUT/file-menu-rows.txt" 2>/dev/null && note "linhas do menu File em file-menu-rows.txt"
  tap FILE_CLOSE_PROJECT; sleep 3
  shot after-close
  if [ -n "${DIALOG_CONFIRM:-}" ]; then tap DIALOG_CONFIRM; else key KEYCODE_ENTER; fi
  sleep 3
  shot final
  end_step OK
}

# ---------------------------------------------------------------------------------------------
say "saida: $OUT"
[ "$DRY" = 1 ] && say "(dry-run: nenhum comando vai para o aparelho; veja commands.txt)"

[ -n "$LOCAL_YYP" ] && cp "$LOCAL_YYP" "$OUT/project.yyp"
want 00 && step_preflight
# Sem o preflight, ainda e preciso ler o .yyp para o passo 03.
[ -s "$OUT/project.yyp" ] || { [ "$DRY" != 1 ] && adb_out exec-out "cat '$PROJECT'" > "$OUT/project.yyp"; }
[ "$DRY" = 1 ] && HAS_KEYCOMBO=1
want 01 && step_open_project
want 02 && step_asset_browser
want 03 && step_open_resources
want 04 && step_create_sprite
want 05 && step_create_object
want 06 && step_script_edit
want 07 && step_preferences
want 08 && step_build_run
want 09 && step_close_project

{
  echo
  echo "Arquivos por passo: \`<passo>-*.png\` (prints), \`<passo>.log\` (logcat filtrado), \`<passo>.errors.txt\`"
  echo "(primeiras linhas de erro), \`<passo>.build.log\` (Build), \`*-rows.txt\` (linhas de menus/popups para calibrar)."
} >> "$OUT/summary.md"
say ""
say "resumo: $OUT/summary.md"
cat "$OUT/summary.md" >&2
