# roda NO APARELHO (adb push + sh). Grava a tela em segmentos de 10 min enquanto /sdcard/gmrec/ON existir.
i=$(ls /sdcard/gmrec/seg_*.mp4 2>/dev/null | wc -l)
while [ -f /sdcard/gmrec/ON ]; do
  i=$((i+1))
  screenrecord --size 1386x640 --bit-rate 6000000 --time-limit 600 /sdcard/gmrec/seg_$(printf %03d $i).mp4
done
