#!/bin/bash
# pan.sh DX  (pixels de tela cheia; negativo = conteudo para a esquerda) usando o modo Mover
d="$(dirname "$0")"
"$d/g.sh" tap 233 746; sleep 1; "$d/g.sh" tap 1288 672; sleep 0.8; "$d/g.sh" tap 233 746; sleep 0.6
"$d/g.sh" swipe 1500 1150 $((1500 + $1)) 1150 700; sleep 0.6
"$d/g.sh" tap 233 746; sleep 1; "$d/g.sh" tap 1288 672; sleep 0.8; "$d/g.sh" tap 233 746; sleep 0.6
