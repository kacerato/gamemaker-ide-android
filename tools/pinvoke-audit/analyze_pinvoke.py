"""Classifica os P/Invokes da IDE e procura checagens de SO no caminho de chamada.

Uso: python -I analyze_pinvoke.py out/   -> escreve out/pinvoke-libs.tsv e out/pinvoke-guards.tsv
Heuristica de guarda: um chamador (ate 4 niveis acima do metodo P/Invoke) que
referencia uma API de deteccao de SO conta como "guardado". Nao prova o ramo
(o IL nao e interpretado), so que existe uma checagem de SO no caminho.
"""
import sys, os, collections

out = sys.argv[1]
def tsv(name):
    with open(os.path.join(out, name), encoding="utf-8") as f:
        for line in f:
            yield line.rstrip("\n").split("\t")

kinds = {r[0]: r[1] for r in tsv("assemblies.tsv")}
DESKTOP = lambda a: a.startswith(("Presentation", "UIAutomation", "System.Windows", "System.Printing")) or a in (
    "ReachFramework", "WindowsFormsIntegration", "DirectWriteForwarder", "Accessibility", "System.Xaml",
    "Microsoft.VisualBasic.Forms", "System.Design", "System.Drawing.Design", "PresentationUI")

APK = {"sdl2": "libSDL2.so", "fna3d": "libFNA3D.so", "freetype": "libfreetype.so", "onig": "libonig.so",
       "yoga": "libyoga.so", "yyal": "libyyal.so", "gmhost": "libgmhost.so",
       "magick.native-q8-arm64": "libMagick.Native-Q8-arm64 (em preparo)"}
ANDROID = {"libc", "libdl", "liblog", "libm", "libandroid", "libglesv3", "libegl", "libz"}
def norm(mod):
    m = mod.lower()
    for suf in (".dll", ".so", ".dylib"):
        if m.endswith(suf): m = m[: -len(suf)]
    return m
def classify(mod):
    n = norm(mod)
    if n in APK: return "APK", APK[n]
    if mod.startswith("/") or "dylib" in mod or "Frameworks" in mod or "objcWrapper" in mod: return "ausente", "macOS"
    if n in ANDROID: return "Android", "bionic"
    return "ausente", ""

GUARDS = ("Core.CoreOS.System.Platform::get_OSType", "Core.CoreOS.System.PlatformProvider::get_OSType",
          "Core.CoreOS.System.IPlatformProvider::get_OSType", "System.OperatingSystem::Is",
          "System.Runtime.InteropServices.RuntimeInformation::IsOSPlatform", "System.Environment::get_OSVersion",
          "System.OperatingSystem::get_Platform", "ImageMagick.Runtime::get_Is", "PlatformConfiguration::get_Is",
          "Steamworks.SteamAPI::Init", "Steamworks.Packsize::Test", "YoYoStudio.Core.Utils.SteamAPI")

callers = collections.defaultdict(set)   # (asm, Type::m) -> {(asm, Type::m)}
guarded_methods = set()
for asm, caller, casm, callee, op in tsv("calls.tsv"):
    callers[(casm, callee)].add((asm, caller))
    if any(g in callee for g in GUARDS):
        guarded_methods.add((asm, caller))

pinv = list(tsv("pinvoke.tsv"))
def chain_status(key, depth=4):
    """'guarded' se todo caminho ate depth encontra guarda; 'nocaller' se ninguem chama; senao 'unguarded'."""
    if not callers.get(key): return "nocaller", []
    seen = {key}; frontier = {key}; open_ends = []
    for d in range(depth):
        nxt = set()
        for k in frontier:
            cs = callers.get(k, ())
            if not cs and k != key:
                open_ends.append(k)          # raiz (entrada publica/evento/reflexao) sem guarda
            for c in cs:
                if c in seen: continue
                seen.add(c)
                if c in guarded_methods: continue
                nxt.add(c)
        frontier = nxt
        if not frontier: break
    open_ends += list(frontier)              # ainda sem guarda no limite de profundidade
    if not open_ends: return "guarded", []
    return "unguarded", sorted(open_ends)[:3]

libs = collections.defaultdict(lambda: {"asms": collections.Counter(), "eps": set(), "st": collections.Counter()})
rows = []
for asm, typ, meth, mod, ep in pinv:
    k = kinds.get(asm, "?")
    if DESKTOP(asm): k = "desktop-fw"
    st, ex = chain_status((asm, f"{typ}::{meth}"))
    L = libs[mod]; L["asms"][f"{asm}({k})"] += 1; L["eps"].add(ep); L["st"][st] += 1
    rows.append((mod, asm, k, typ, meth, ep, st, " | ".join(f"{a}:{m}" for a, m in ex)))

with open(os.path.join(out, "pinvoke-guards.tsv"), "w", encoding="utf-8") as f:
    f.write("module\tasm\tasmkind\ttype\tmethod\tentrypoint\tstatus\tunguarded_frontier\n")
    for r in sorted(rows): f.write("\t".join(r) + "\n")
with open(os.path.join(out, "pinvoke-libs.tsv"), "w", encoding="utf-8") as f:
    f.write("module\tclass\tnote\tn_imports\tassemblies\tstatus_counts\tsample_entrypoints\n")
    for mod, L in sorted(libs.items(), key=lambda kv: -sum(kv[1]["asms"].values())):
        c, note = classify(mod)
        f.write(f"{mod}\t{c}\t{note}\t{sum(L['asms'].values())}\t{', '.join(f'{a}x{n}' for a, n in L['asms'].most_common())}\t"
                f"{dict(L['st'])}\t{', '.join(sorted(L['eps'])[:8])}\n")
print("ok", len(rows))
