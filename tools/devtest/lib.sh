# Funcoes do roteiro tools/devtest (Git Bash). Use via "source lib.sh"; nao execute direto.
#
# Variaveis que o chamador define antes: OUT (pasta de saida), DRY (1 = so imprime os comandos),
# HERE (pasta deste arquivo). coords.conf ja deve ter sido lido.

ADB="${ADB:-$LOCALAPPDATA/Android/Sdk/platform-tools/adb.exe}"
PKG="io.gmide.android"
ACTIVITY="io.gmide.android/io.gmide.GmIdeActivity"
PY="${PY:-python}"
REF_PNG="${REF_PNG:-$HERE/../../logs/run14.png}"   # usado no modo --dry-run no lugar do screencap

# Linhas de logcat que interessam por passo (o resto do sistema e ruido).
LOG_FILTER='GmIde|DOTNET|monodroid|mono-rt|SDL|yyal|AndroidRuntime|FATAL|DEBUG  |libc  |F/|io\.gmide'
# O que conta como erro da IDE dentro do trecho de log de um passo.
ERR_FILTER='Exception|FATAL|Fatal signal|SIGSEGV|SIGABRT|DllNotFound|EntryPointNotFound|PlatformNotSupported'

STEP_N=-1   # o primeiro passo (preflight) vira 00
STEP_ID=""
STEP_T0=""
STEP_FIRST_SHOT=""
STEP_LAST_SHOT=""

# No dry-run as esperas nao servem para nada: sleep vira no-op.
if [ "${DRY:-0}" = 1 ]; then sleep() { :; }; fi

say()  { printf '%s\n' "$*" | tee -a "$OUT/run.txt" >&2; }

# adb sem conversao de caminho do MSYS (senao /storage/... vira C:/Program Files/Git/storage/...).
adb_() {
  if [ "${DRY:-0}" = 1 ]; then
    printf 'adb %s\n' "$*" >> "$OUT/commands.txt"
    return 0
  fi
  MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*' "$ADB" "$@"
}

# Saida de um comando no aparelho (no dry-run devolve vazio).
adb_out() {
  if [ "${DRY:-0}" = 1 ]; then printf 'adb %s\n' "$*" >> "$OUT/commands.txt"; return 0; fi
  MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*' "$ADB" "$@" 2>/dev/null | tr -d '\r'
}

dev_time() {
  if [ "${DRY:-0}" = 1 ]; then date '+%m-%d %H:%M:%S.000'; return; fi
  adb_out shell "date '+%m-%d %H:%M:%S.000'"
}

app_pid()   { adb_out shell pidof "$PKG"; }
app_alive() { [ "${DRY:-0}" = 1 ] && return 0; [ -n "$(app_pid)" ]; }

# --- entrada -----------------------------------------------------------------

xy() { # NOME_DA_VARIAVEL -> "x y"
  local v="${!1:-}"
  [ -n "$v" ] || { say "  !! coordenada $1 vazia no coords.conf"; return 1; }
  printf '%s' "$v"
}

tap_xy() { adb_ shell input tap "$1" "$2"; sleep "${TAP_PAUSE:-0.6}"; }

# tap NOME: tenta o template (templates/<nome>.png) perto da posicao esperada; senao usa coords.conf.
tap() {
  local name="$1" pos x y tpl res tx ty score
  pos=$(xy "$name") || return 1
  read -r x y <<<"$pos"
  tpl="$HERE/templates/$(printf '%s' "${name#TB_}" | tr 'A-Z' 'a-z').png"
  if [ "${DRY:-0}" != 1 ] && [ -f "$tpl" ] && [ -n "$STEP_LAST_SHOT" ] && [ -f "$STEP_LAST_SHOT" ]; then
    res=$("$PY" -I "$HERE/screen.py" locate "$STEP_LAST_SHOT" "$tpl" "$x" "$y" "${TPL_SEARCH_RADIUS:-300}" 2>/dev/null)
    read -r tx ty score <<<"$res"
    if [ -n "${score:-}" ] && awk -v s="$score" -v m="${TPL_MIN_SCORE:-0.8}" 'BEGIN{exit !(s>=m)}'; then
      say "  tap $name: template em ($tx,$ty) score=$score (conf: $x,$y)"
      x=$tx; y=$ty
    else
      say "  tap $name: template nao casou (score=${score:-?}); usando coords.conf ($x,$y)"
    fi
  else
    say "  tap $name ($x,$y)"
  fi
  tap_xy "$x" "$y"
}

key() { # key KEYCODE_X [KEYCODE_Y...] (teclas separadas, uma depois da outra)
  say "  key $*"
  adb_ shell input keyevent "$@"
  sleep "${KEY_PAUSE:-0.5}"
}

# combo KEYCODE_CTRL_LEFT KEYCODE_S: segura os modificadores e aperta a ultima tecla.
# Usa "input keycombination" (Android 13+). O SDL recebe KEYDOWN de CTRL antes da letra, entao
# o estado de modificador da IDE fica certo.
combo() {
  say "  combo $*"
  if [ "${DRY:-0}" = 1 ]; then adb_ shell input keycombination "$@"; return 0; fi
  if [ "${HAS_KEYCOMBO:-1}" = 1 ]; then
    adb_ shell input keycombination "$@" >/dev/null 2>&1 && { sleep "${KEY_PAUSE:-0.5}"; return 0; }
  fi
  say "  !! input keycombination indisponivel; atalho $* nao enviado"
  return 1
}

# Digita texto ASCII simples. Espaco vira %s; caracteres especiais do shell sao escapados.
type_text() {
  local t="$1" e
  e=$(printf '%s' "$t" | sed -e 's/[][()<>|;&*\\"`$!?#~{}]/\\&/g' -e "s/'/\\\\'/g" -e 's/ /%s/g')
  say "  texto: $t"
  adb_ shell input text "$e"
  sleep "${KEY_PAUSE:-0.5}"
}

# --- prints e logs -------------------------------------------------------------

shot() { # shot ROTULO -> $OUT/<passo>-<rotulo>.png
  local f="$OUT/${STEP_ID}-$1.png"
  if [ "${DRY:-0}" = 1 ]; then
    cp "$REF_PNG" "$f" 2>/dev/null || : > "$f"
    printf 'adb exec-out screencap -p > %s\n' "$f" >> "$OUT/commands.txt"
  else
    MSYS_NO_PATHCONV=1 "$ADB" exec-out screencap -p > "$f" 2>/dev/null
  fi
  [ -z "$STEP_FIRST_SHOT" ] && STEP_FIRST_SHOT="$f"
  STEP_LAST_SHOT="$f"
  say "  print: $(basename "$f")"
}

# Trecho de logcat desde o inicio do passo (filtrado) em $OUT/<passo>.log
step_log() {
  local f="$OUT/${STEP_ID}.log"
  if [ "${DRY:-0}" = 1 ]; then : > "$f"; printf 'adb exec-out logcat -d -v threadtime -T %s\n' "$STEP_T0" >> "$OUT/commands.txt"; return; fi
  adb_out exec-out "logcat -d -v threadtime -T '$STEP_T0'" | grep -E "$LOG_FILTER" > "$f" || true
}

# wait_log REGEX SEGUNDOS: espera uma linha de log (desde o inicio do passo). 0 = achou.
wait_log() {
  local re="$1" max="$2" t=0
  [ "${DRY:-0}" = 1 ] && { printf 'wait_log %s %s\n' "$re" "$max" >> "$OUT/commands.txt"; return 0; }
  while [ "$t" -lt "$max" ]; do
    if adb_out exec-out "logcat -d -v threadtime -T '$STEP_T0'" | grep -E -m1 "$re" > "$OUT/${STEP_ID}.match" 2>/dev/null; then
      say "  log: $(cut -c1-200 "$OUT/${STEP_ID}.match")"
      return 0
    fi
    app_alive || { say "  !! app morreu enquanto esperava '$re'"; return 2; }
    sleep 3; t=$((t + 3))
  done
  say "  !! tempo esgotado (${max}s) esperando '$re'"
  return 1
}

begin_step() { # begin_step NOME "descricao"
  STEP_N=$((STEP_N + 1))
  STEP_ID=$(printf '%02d-%s' "$STEP_N" "$1")
  STEP_DESC="$2"
  STEP_T0=$(dev_time)
  STEP_FIRST_SHOT=""
  STEP_NOTE=""
  say ""
  say "== $STEP_ID: $STEP_DESC (aparelho: $STEP_T0)"
}

note() { STEP_NOTE="${STEP_NOTE:+$STEP_NOTE; }$*"; say "  nota: $*"; }

# end_step [STATUS_SUGERIDO]: fecha o passo, decide o status e grava uma linha no summary.md.
#   STATUS_SUGERIDO = OK | FAIL | TIMEOUT | SKIP (o passo pode forcar). CRASH sempre vence.
end_step() {
  local st="${1:-OK}" errs=0 changed="-" d
  step_log
  [ -s "$OUT/${STEP_ID}.log" ] && errs=$(grep -c -E "$ERR_FILTER" "$OUT/${STEP_ID}.log" || true)
  if [ -n "$STEP_FIRST_SHOT" ] && [ "$STEP_FIRST_SHOT" != "$STEP_LAST_SHOT" ] && [ "${DRY:-0}" != 1 ]; then
    d=$("$PY" -I "$HERE/screen.py" diff "$STEP_FIRST_SHOT" "$STEP_LAST_SHOT" 2>/dev/null | awk '{print $1}')
    changed="${d:-?}"
  fi
  if ! app_alive; then
    st="CRASH"
    adb_out exec-out "logcat -d -v threadtime -b crash -T '$STEP_T0'" > "$OUT/${STEP_ID}.crash.log" || true
  elif [ "$st" = OK ] && [ "$errs" -gt 0 ]; then
    st="WARN"
  fi
  [ "$st" = OK ] && [ "$changed" = "0" ] && st="WARN" && note "tela nao mudou"
  if [ "$errs" -gt 0 ]; then
    grep -E "$ERR_FILTER" "$OUT/${STEP_ID}.log" | cut -c1-240 | head -5 > "$OUT/${STEP_ID}.errors.txt"
  fi
  printf '| %s | %s | %s | %s | %s | %s |\n' "$STEP_ID" "$STEP_DESC" "$st" "$changed" "$errs" "${STEP_NOTE:-}" >> "$OUT/summary.md"
  say "-> $STEP_ID: $st (tela mudou: $changed, erros no log: $errs)"
  [ "$st" = CRASH ] && return 2
  return 0
}

# Se o app morreu, reabre com o projeto (para os passos seguintes ainda rodarem).
ensure_running() {
  app_alive && return 0
  say "  app nao esta rodando; reabrindo com o projeto"
  local saved_t0="$STEP_T0" rc=0
  STEP_T0=$(dev_time)
  adb_ shell am start -n "$ACTIVITY" --es project "'$PROJECT'" >/dev/null
  wait_log "Project Loaded" "${WAIT_PROJECT:-240}" || rc=1
  STEP_T0="$saved_t0"
  sleep 3
  return $rc
}
