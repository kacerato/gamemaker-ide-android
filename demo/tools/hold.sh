#!/bin/bash
# hold.sh X Y SEG -> segura o dedo num ponto
export MSYS_NO_PATHCONV=1; ADB="$LOCALAPPDATA/Android/Sdk/platform-tools/adb.exe"
"$ADB" shell "input motionevent DOWN $1 $2; sleep $3; input motionevent UP $1 $2"
