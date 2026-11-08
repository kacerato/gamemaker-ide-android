#!/usr/bin/env python3
"""Utilitarios de imagem para o roteiro tools/devtest (rodam no PC, nunca no aparelho).

Subcomandos:
  diff A.png B.png                 -> "<fracao_mudada> <x0> <y0> <x1> <y1>" (bbox da mudanca; "0 - - - -" se igual)
  locate SHOT.png TEMPLATE.png [X Y RAIO]
                                   -> "<x> <y> <score>" centro do melhor casamento (score 0..1, 1 = identico);
                                      com X Y RAIO procura so em volta da posicao esperada
  rows BEFORE.png AFTER.png        -> linhas de texto do popup/menu que apareceu entre BEFORE e AFTER:
                                      "<idx> <y_centro> <x0> <x1>" (uma por linha), ordenadas de cima para baixo
  crop SRC.png COORDS.conf OUTDIR  -> recorta templates (TPL_<NOME>="x y meia_largura") de um print de referencia
  info SHOT.png                    -> "<largura> <altura>"

Precisa de Pillow e numpy (pip install pillow numpy). Sem eles, os subcomandos saem com codigo 3 e o
devtest.sh cai nas coordenadas fixas do coords.conf.
"""
import sys

try:
    import numpy as np
    from PIL import Image
except ImportError:  # pragma: no cover
    sys.stderr.write("screen.py: Pillow/numpy ausentes\n")
    sys.exit(3)


def load_gray(path):
    return np.asarray(Image.open(path).convert("L"), dtype=np.float32)


def load_rgb(path):
    return np.asarray(Image.open(path).convert("RGB"), dtype=np.int16)


def cmd_diff(a, b, thr=24):
    A, B = load_rgb(a), load_rgb(b)
    if A.shape != B.shape:
        print("1.0 0 0 %d %d" % (B.shape[1] - 1, B.shape[0] - 1))
        return
    mask = np.abs(A - B).max(axis=2) > thr
    frac = float(mask.mean())
    if not mask.any():
        print("0 - - - -")
        return
    ys, xs = np.nonzero(mask)
    print("%.5f %d %d %d %d" % (frac, xs.min(), ys.min(), xs.max(), ys.max()))


def _sad_best(img, t):
    """Melhor posicao (menor diferenca media absoluta) de t dentro de img. Vetorizado."""
    from numpy.lib.stride_tricks import sliding_window_view
    h, w = t.shape
    if h > img.shape[0] or w > img.shape[1]:
        return (1e18, 0, 0)
    win = sliding_window_view(img, (h, w))
    d = np.abs(win - t).mean(axis=(2, 3))
    y, x = np.unravel_index(int(np.argmin(d)), d.shape)
    return (float(d[y, x]), int(x), int(y))


def cmd_locate(shot, tpl, hint=None, scale=4):
    """Casamento por diferenca absoluta: busca grossa reduzida, depois refino local.

    hint = (x, y, raio): procura so num quadrado em volta da posicao esperada (mais rapido e
    evita casar com um icone parecido em outro lugar)."""
    S, T = load_gray(shot), load_gray(tpl)
    th, tw = T.shape
    ox = oy = 0
    if hint:
        hx, hy, hr = hint
        ox, oy = max(0, hx - hr - tw), max(0, hy - hr - th)
        S = S[oy:hy + hr + th, ox:hx + hr + tw]
    if th > S.shape[0] or tw > S.shape[1]:
        print("- - 0")
        return
    _, xs, ys = _sad_best(S[::scale, ::scale], T[::scale, ::scale])
    x0, y0 = xs * scale, ys * scale
    r = scale * 2
    bx, by = max(0, x0 - r), max(0, y0 - r)
    d, xr, yr = _sad_best(S[by:y0 + th + r, bx:x0 + tw + r], T)
    x, y = ox + bx + xr, oy + by + yr
    score = max(0.0, 1.0 - d / 255.0 * 4)  # 1 = identico; abaixo de ~0.8 costuma ser casamento falso
    print("%d %d %.3f" % (x + tw // 2, y + th // 2, score))


def cmd_rows(before, after, thr=24, min_h=8):
    A, B = load_rgb(before), load_rgb(after)
    if A.shape != B.shape:
        sys.exit("tamanhos diferentes")
    mask = np.abs(A - B).max(axis=2) > thr
    if not mask.any():
        return
    ys, xs = np.nonzero(mask)
    x0, x1, y0, y1 = xs.min(), xs.max(), ys.min(), ys.max()
    region = np.asarray(Image.open(after).convert("L"), dtype=np.float32)[y0:y1 + 1, x0:x1 + 1]
    # fundo = mediana da regiao; "tinta" = pixels bem diferentes do fundo (texto/icones)
    bg = np.median(region)
    ink = np.abs(region - bg) > 60
    prof = ink.sum(axis=1) > max(2, ink.shape[1] * 0.004)
    rows, start = [], None
    for i, v in enumerate(list(prof) + [False]):
        if v and start is None:
            start = i
        elif not v and start is not None:
            if i - start >= min_h:
                rows.append((start, i - 1))
            start = None
    # junta linhas muito proximas (letras com acento/descendentes)
    merged = []
    for r in rows:
        if merged and r[0] - merged[-1][1] < 4:
            merged[-1] = (merged[-1][0], r[1])
        else:
            merged.append(r)
    for idx, (a, b) in enumerate(merged):
        print("%d %d %d %d" % (idx, y0 + (a + b) // 2, x0, x1))


def read_conf(path):
    conf = {}
    for line in open(path, encoding="utf-8"):
        line = line.split("#", 1)[0].strip()
        if "=" not in line:
            continue
        k, v = line.split("=", 1)
        conf[k.strip()] = v.strip().strip('"')
    return conf


def cmd_crop(src, conf_path, outdir):
    import os
    os.makedirs(outdir, exist_ok=True)
    img = Image.open(src).convert("RGB")
    n = 0
    for k, v in read_conf(conf_path).items():
        if not k.startswith("TPL_"):
            continue
        parts = v.split()
        if len(parts) != 3:
            continue
        x, y, r = map(int, parts)
        img.crop((x - r, y - r, x + r, y + r)).save(os.path.join(outdir, k[4:].lower() + ".png"))
        n += 1
    print("%d templates em %s" % (n, outdir))


def main(argv):
    if len(argv) < 2:
        sys.exit(__doc__)
    c, a = argv[1], argv[2:]
    if c == "diff" and len(a) == 2:
        cmd_diff(*a)
    elif c == "locate" and len(a) in (2, 5):
        cmd_locate(a[0], a[1], tuple(map(int, a[2:5])) if len(a) == 5 else None)
    elif c == "rows" and len(a) == 2:
        cmd_rows(*a)
    elif c == "crop" and len(a) == 3:
        cmd_crop(*a)
    elif c == "info" and len(a) == 1:
        w, h = Image.open(a[0]).size
        print(w, h)
    else:
        sys.exit(__doc__)


if __name__ == "__main__":
    main(sys.argv)
