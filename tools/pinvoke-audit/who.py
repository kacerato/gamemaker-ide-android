"""Arvore de chamadores de um metodo/tipo, marcando checagens de SO.
Uso: python -I who.py out/ <substring> [<substring> ...] [--depth N] [--callees]
  <substring> casa com 'Tipo::metodo' do alvo (calls.tsv col 4).
  --callees mostra o que o metodo chama (filtra 'Tipo::metodo' do chamador).
Marca [G] em metodos que referenciam API de deteccao de SO.
"""
import sys, os, collections
out = sys.argv[1]; args = sys.argv[2:]
depth = 3; callees_mode = False
if "--depth" in args:
    i = args.index("--depth"); depth = int(args[i + 1]); del args[i:i + 2]
if "--callees" in args:
    args.remove("--callees"); callees_mode = True
GUARDS = ("Platform::get_OSType", "PlatformProvider::get_OSType", "System.OperatingSystem::Is",
          "RuntimeInformation::IsOSPlatform", "System.Environment::get_OSVersion", "System.OperatingSystem::get_Platform",
          "ImageMagick.Runtime::get_Is", "PlatformConfiguration::get_Is")
callers = collections.defaultdict(set); callees = collections.defaultdict(list); guarded = set()
with open(os.path.join(out, "calls.tsv"), encoding="utf-8") as f:
    for line in f:
        asm, caller, casm, callee, op = line.rstrip("\n").split("\t")
        callers[callee].add(f"{asm}:{caller}")
        callees[f"{asm}:{caller}"].append(f"{casm}:{callee} ({op})")
        if any(g in callee for g in GUARDS): guarded.add(f"{asm}:{caller}")
def mark(k): return " [G]" if k in guarded else ""
if callees_mode:
    for q in args:
        for k in sorted(callees):
            if q in k:
                print(f"== {k}{mark(k)}")
                seen = set()
                for c in callees[k]:
                    if c not in seen: seen.add(c); print("   ", c)
    sys.exit()
for q in args:
    targets = sorted({c for c in callers if q in c})
    for t in targets[:20]:
        print(f"== {t}")
        def rec(name, d, seen):
            if d > depth: return
            for c in sorted(callers.get(name, ())):
                if c in seen: continue
                seen.add(c)
                print("   " * d + f"<- {c}{mark(c)}")
                if c not in guarded: rec(c.split(":", 1)[1], d + 1, seen)
        rec(t, 1, set())
