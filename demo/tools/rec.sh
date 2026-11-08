#!/bin/bash
# uso: rec.sh start|stop|pull
export MSYS_NO_PATHCONV=1
ADB="$LOCALAPPDATA/Android/Sdk/platform-tools/adb.exe"
here="$(cd "$(dirname "$0")" && pwd)"
case "$1" in
  start)
    "$ADB" push "$(cygpath -w "$here/rec-device.sh")" /sdcard/gmrec/rec-device.sh >/dev/null
    "$ADB" shell "touch /sdcard/gmrec/ON; nohup sh /sdcard/gmrec/rec-device.sh >/dev/null 2>&1 &"
    echo "gravando"; date +%s > "$here/rec-start.txt" ;;
  stop)
    "$ADB" shell "rm -f /sdcard/gmrec/ON; pkill -INT screenrecord"; sleep 2
    "$ADB" shell ls -la /sdcard/gmrec ;;
  pull)
    mkdir -p "$here/../rec"
    for f in $("$ADB" shell ls /sdcard/gmrec/ | tr -d '\r' | grep '^seg_'); do
      "$ADB" pull "/sdcard/gmrec/$f" "$(cygpath -w "$here/../rec/$f")" >/dev/null && echo "$f"
    done ;;
esac
