#!/bin/bash
# helper de automacao: g.sh tap X Y | swipe X1 Y1 X2 Y2 MS | key K... | text "..." | shot NOME | fg | mark "evento"
export MSYS_NO_PATHCONV=1
ADB="$LOCALAPPDATA/Android/Sdk/platform-tools/adb.exe"
here="$(cd "$(dirname "$0")" && pwd)"
SH="$here/../shots"; mkdir -p "$SH"
fg() { "$ADB" shell dumpsys activity activities | grep -m1 topResumedActivity | grep -q "io.gmide"; }
case "$1" in
  fg) if fg; then echo FRONT; else "$ADB" shell dumpsys activity activities | grep -m1 topResumedActivity; fi ;;
  rtap) "$ADB" shell input tap "$2" "$3" ;;
  tap) fg || { echo "IDE nao esta na frente"; exit 3; }; "$ADB" shell input tap "$2" "$3" ;;
  dtap) fg || exit 3; "$ADB" shell "input tap $2 $3; input tap $2 $3" ;;
  long) fg || exit 3; "$ADB" shell input swipe "$2" "$3" "$2" "$3" "${4:-800}" ;;
  swipe) fg || exit 3; "$ADB" shell input swipe "$2" "$3" "$4" "$5" "${6:-400}" ;;
  key) fg || exit 3; shift; "$ADB" shell input keyevent "$@" ;;
  text) fg || exit 3; t="$2"; t="${t// /%s}"; t="${t//\'/\'\\'\'}"; "$ADB" shell "input text '$t'" ;;
  shot) "$ADB" exec-out screencap -p > "$SH/$2.png"; ffmpeg -v error -y -i "$(cygpath -w "$SH/$2.png")" -vf scale=1386:-1 "$(cygpath -w "$SH/$2_s.png")"; cygpath -w "$SH/$2_s.png" ;;
  mark) echo "$(date +%s) $2" >> "$here/marks.txt" ;;
esac
