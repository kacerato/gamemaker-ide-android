#!/bin/bash
# ui.sh find "texto" -> imprime centro "x y" do primeiro no com esse texto (exato)
export MSYS_NO_PATHCONV=1
ADB="$LOCALAPPDATA/Android/Sdk/platform-tools/adb.exe"
"$ADB" shell "uiautomator dump /sdcard/ui.xml >/dev/null 2>&1; cat /sdcard/ui.xml" | tr '>' '\n' | grep -F "text=\"$2\"" | head -1 | sed -E 's/.*bounds="\[([0-9]+),([0-9]+)\]\[([0-9]+),([0-9]+)\]".*/\1 \2 \3 \4/' | awk '{if(NF==4) print int(($1+$3)/2), int(($2+$4)/2)}'
