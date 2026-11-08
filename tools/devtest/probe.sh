#!/usr/bin/env bash
# Ajuda a calibrar o coords.conf: toca num ponto (nome do coords.conf ou "x y"), tira print antes
# e depois e mostra o que mudou na tela e as linhas do menu/popup que abriu.
#
#   bash tools/devtest/probe.sh MENU_FILE
#   bash tools/devtest/probe.sh 120 520
#   bash tools/devtest/probe.sh --key KEYCODE_CTRL_LEFT KEYCODE_T     # testa um atalho em vez de toque
#
# Saida em logs/devtest/probe-<hora>/ (before.png, after.png, rows.txt).
set -u
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
OUT="$ROOT/logs/devtest/probe-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$OUT"
DRY=0
export OUT DRY HERE
source "$HERE/coords.conf"
source "$HERE/lib.sh"
STEP_ID="probe"

[ $# -ge 1 ] || { sed -n '2,9p' "$0" | sed 's/^# \{0,1\}//'; exit 1; }

shot before
if [ "$1" = --key ]; then
  shift; combo "$@"
elif [ $# -ge 2 ] && [ "$1" -eq "$1" ] 2>/dev/null; then
  tap_xy "$1" "$2"
else
  tap "$1"
fi
sleep "${WAIT_SHORT:-2}"
shot after
echo "mudanca (fracao x0 y0 x1 y1): $("$PY" -I "$HERE/screen.py" diff "$OUT/probe-before.png" "$OUT/probe-after.png")"
"$PY" -I "$HERE/screen.py" rows "$OUT/probe-before.png" "$OUT/probe-after.png" | tee "$OUT/rows.txt"
echo "(linhas: indice y_centro x0 x1 — use \"x y_centro\" no coords.conf)"
echo "prints em $OUT"
