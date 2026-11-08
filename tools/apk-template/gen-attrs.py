#!/usr/bin/env python3
"""
Gera host/ApkExport/ManifestAttrs.g.cs: id, formato, enums e flags de cada atributo android:* usado em
AndroidManifest.xml (declare-styleable AndroidManifest* do SDK). O compilador de manifesto do app
(host/ApkExport/Axml.cs) usa a tabela para gravar o XML binario igual ao aapt2.

Uso: python tools/apk-template/gen-attrs.py [--api 35]
"""
import argparse, os, re, xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
FORMAT_BITS = {  # android::ResTable_map::TYPE_*
    "reference": 1 << 0, "string": 1 << 1, "integer": 1 << 2, "boolean": 1 << 3, "color": 1 << 4,
    "float": 1 << 5, "dimension": 1 << 6, "fraction": 1 << 7, "enum": 1 << 16, "flags": 1 << 17,
}


def parse_int(s):
    s = s.strip()
    return int(s, 16) if s.lower().startswith("0x") else int(s)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--api", default="35")
    a = ap.parse_args()
    vals = Path(os.environ["LOCALAPPDATA"]) / "Android" / "Sdk" / "platforms" / f"android-{a.api}" / "data" / "res" / "values"
    ids = {}
    fw = {}  # recursos publicos do framework usados em manifestos (@android:style/...)
    for pub in ("public-final.xml", "public-staging.xml"):
        p = vals / pub
        if p.exists():
            for el in ET.parse(p).getroot().iter("public"):
                if not el.get("id"):
                    continue
                if el.get("type") == "attr":
                    ids[el.get("name")] = parse_int(el.get("id"))
                elif el.get("type") in ("style", "drawable", "mipmap", "string", "color", "xml"):
                    fw[el.get("type") + "/" + el.get("name")] = parse_int(el.get("id"))
    defs = {}
    for f in ("attrs.xml", "attrs_manifest.xml"):
        for el in ET.parse(vals / f).getroot().iter("attr"):
            name = el.get("name")
            if not name or name.startswith("android:"):
                continue
            fmt = el.get("format")
            enums = {e.get("name"): parse_int(e.get("value")) for e in el.findall("enum")}
            flags = {e.get("name"): parse_int(e.get("value")) & 0xFFFFFFFF for e in el.findall("flag")}
            if fmt is None and not enums and not flags:
                continue
            mask = 0
            for part in (fmt or "").split("|"):
                mask |= FORMAT_BITS.get(part.strip(), 0)
            if enums:
                mask |= FORMAT_BITS["enum"]
            if flags:
                mask |= FORMAT_BITS["flags"]
            # attrs_manifest.xml vence attrs.xml (lido depois)
            defs[name] = (mask, enums, flags)
    used = set()
    for st in ET.parse(vals / "attrs_manifest.xml").getroot().iter("declare-styleable"):
        if st.get("name", "").startswith("AndroidManifest"):
            for el in st.iter("attr"):
                n = el.get("name")
                if n and not n.startswith("android:"):
                    used.add(n)
    lines = [
        "// <auto-gerado> por tools/apk-template/gen-attrs.py (android-%s). Nao editar." % a.api,
        "namespace GmIde;",
        "",
        "static partial class ManifestAttrs",
        "{",
        "    static void Fill(Dictionary<string, Attr> d)",
        "    {",
    ]
    def cs(s):
        return '"' + s.replace("\\", "\\\\").replace('"', '\\"') + '"'
    for n in sorted(used):
        if n not in ids or n not in defs:
            continue
        mask, enums, flags = defs[n]
        e = "null" if not enums else "new() { " + ", ".join(f"[{cs(k)}] = {v}" for k, v in enums.items()) + " }"
        fl = "null" if not flags else "new() { " + ", ".join(f"[{cs(k)}] = 0x{v:08x}u" for k, v in flags.items()) + " }"
        lines.append(f"        d[{cs(n)}] = new Attr(0x{ids[n]:08x}, 0x{mask:x}, {e}, {fl});")
    lines += ["    }", "", "    static void FillFramework(Dictionary<string, uint> d)", "    {"]
    for k in sorted(fw):
        lines.append(f"        d[{cs(k)}] = 0x{fw[k]:08x};")
    lines += ["    }", "}", ""]
    out = ROOT / "host" / "ApkExport" / "ManifestAttrs.g.cs"
    out.write_text("\n".join(lines), encoding="utf-8")
    print(f"{out}: {len(lines) - 10} atributos")


if __name__ == "__main__":
    main()
