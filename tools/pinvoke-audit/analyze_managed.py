"""APIs gerenciadas arriscadas no Android + alcancabilidade a partir do boot.
Uso: python -I analyze_managed.py out/
Escreve out/managed-risk.tsv (todas as refs arriscadas) e out/managed-risk-boot.tsv
(as que estao em metodos alcancaveis a partir das raizes de boot no grafo call/newobj/ldftn).
O grafo so segue chamadas diretas (call/callvirt/newobj/ldftn) ao metodo declarado;
implementacoes de interface/virtual e reflexao nao sao seguidas.
"""
import sys, os, collections, re
out = sys.argv[1]
def tsv(name):
    with open(os.path.join(out, name), encoding="utf-8") as f:
        for line in f: yield line.rstrip("\n").split("\t")
kinds = {r[0]: r[1] for r in tsv("assemblies.tsv")}
DESK = lambda a: a.startswith(("Presentation", "UIAutomation", "System.Windows", "System.Printing")) or a in (
    "ReachFramework", "WindowsFormsIntegration", "DirectWriteForwarder", "Accessibility", "System.Xaml",
    "Microsoft.VisualBasic.Forms", "System.Design", "System.Drawing.Design", "PresentationUI", "GameMaker-LTS2026",
    "PdbGenerator", "envdte")

# (regex sobre "asm|Tipo::membro", categoria, efeito no Android)
RULES = [
    (r"^Microsoft\.Win32\.Registry\|", "Registry", "PNSE: Registry.* lanca PlatformNotSupportedException fora do Windows"),
    (r"^System\.Management\|", "System.Management", "assembly bloqueado pelo lancador -> FileNotFoundException; mesmo carregado: PNSE"),
    (r"^System\.Diagnostics\.EventLog\|", "EventLog", "assembly bloqueado; EventLog e Windows-only"),
    (r"System\.Security\.Principal\.Windows(Identity|Principal)", "WindowsIdentity", "PNSE fora do Windows"),
    (r"AccessControl|FileSystemSecurity|PipeSecurity|MutexSecurity|DirectorySecurity|FileSecurity", "ACL", "PNSE fora do Windows"),
    (r"^System\.Drawing\.Common\|", "System.Drawing.Common", "assembly bloqueado; .NET 8 so suporta no Windows (TypeInitializationException)"),
    (r"^System\.Windows\.Forms|^PresentationFramework\||^PresentationCore\||^WindowsBase\|", "WinForms/WPF", "nao existe no Android"),
    (r"System\.Console::(ReadKey|get_KeyAvailable|get_WindowWidth|get_WindowHeight|get_BufferWidth|set_Title|get_Title|Beep|SetWindowSize|get_CursorLeft|get_CursorTop|SetCursorPosition|get_In|ReadLine|Read$|set_TreatControlCAsInput|get_ForegroundColor|set_ForegroundColor|ResetColor|Clear)", "Console interativo", "sem terminal: retorna nulo/IOException ou PNSE (ex. Console.Title get)"),
    (r"System\.Diagnostics\.Process::(get_MainWindowHandle|get_MainWindowTitle|CloseMainWindow|get_PriorityBoostEnabled|set_ProcessorAffinity|get_ProcessorAffinity|get_SessionId|get_Modules|get_MainModule)", "Process (Windows)", "PNSE ou dados vazios no Android"),
    (r"System\.Diagnostics\.ProcessStartInfo::(set_Verb|set_UseShellExecute|set_LoadUserProfile|set_Domain|set_UserName|set_Password|set_CreateNoWindow|set_WindowStyle)", "ProcessStartInfo", "Verb/LoadUserProfile/credenciais: PNSE; UseShellExecute=true abre via xdg-open (nao existe)"),
    (r"System\.Diagnostics\.Process::Start", "Process.Start", "fork/exec funciona, mas o executavel alvo (Windows .exe/xdg-open/ sh scripts) nao existe"),
    (r"System\.IO\.Pipes\.NamedPipe(Server|Client)Stream::\.ctor", "Named pipes", "no Unix vira socket de dominio em TMPDIR; com PipeSecurity/PipeOptions.CurrentUserOnly pode lancar"),
    (r"System\.Runtime\.InteropServices\.Marshal::(GetHRForLastWin32Error|GetActiveObject|ReleaseComObject|FinalReleaseComObject|GetIDispatchForObject|GetIUnknownForObject|BindToMoniker)", "COM", "PNSE fora do Windows"),
    (r"System\.Threading\.(Mutex|Semaphore|EventWaitHandle)::(\.ctor|OpenExisting|TryOpenExisting)", "Named sync", "nomes 'Global\\' viram arquivo em TMPDIR; Semaphore/EventWaitHandle nomeados: PNSE no Unix"),
    (r"System\.Environment::GetFolderPath|System\.Environment/SpecialFolder", "SpecialFolder", "mapeia para $HOME/XDG; varios valores viram string vazia"),
    (r"System\.Environment::(get_UserDomainName|get_SystemDirectory)", "Environment Windows", "valores vazios/sem sentido no Android"),
    (r"System\.IO\.DriveInfo", "DriveInfo", "le /proc/mounts; ok mas sem letras de unidade"),
    (r"System\.Net\.NetworkInformation\.(NetworkInterface|Ping|IPGlobalProperties)", "NetworkInformation", "Android restringe /proc/net e netlink (SELinux): pode lancar NetworkInformationException"),
    (r"System\.Security\.Cryptography\.(ProtectedData|DataProtectionScope)", "DPAPI", "PNSE fora do Windows"),
    (r"System\.Security\.Cryptography\.X509Certificates\.X509Certificate::CreateFromSignedFile", "Authenticode", "PNSE fora do Windows"),
    (r"System\.Security\.Cryptography\.X509Certificates\.X509Store::(\.ctor|Open)", "X509Store", "Android: so stores limitados; My/CurrentUser read-only"),
    (r"System\.Runtime\.Loader\.AssemblyLoadContext|System\.Reflection\.Assembly::(LoadFrom|LoadFile|Load|UnsafeLoadFrom)|System\.AppDomain::(Load|get_BaseDirectory)", "Assembly.Load*", "ok no Mono, mas caminhos montados com '\\\\' falham"),
    (r"System\.Runtime\.InteropServices\.NativeLibrary::(Load|TryLoad|SetDllImportResolver)", "NativeLibrary", "resolucao de libs nativas propria: confira nomes"),
    (r"System\.Diagnostics\.(PerformanceCounter|FileVersionInfo::GetVersionInfo)", "Diag", "PerformanceCounter: assembly bloqueado; FileVersionInfo ok (le PE)"),
    (r"System\.IO\.FileSystemWatcher::\.ctor", "FileSystemWatcher", "inotify: ok, mas limite de watches e /sdcard via FUSE nao gera eventos de outras apps"),
]
blocked_asms = {a for a, k in kinds.items() if k == "blocked-by-launcher"}
for a in blocked_asms:
    if not any(re.escape(a) in r[0] for r in RULES):
        RULES.append((r"^" + re.escape(a) + r"\|", "assembly bloqueado: " + a, "Launcher.ResolveFromIdeDir devolve null -> FileNotFoundException"))
RULES = [(re.compile(p), c, e) for p, c, e in RULES]

ROOTS = ["IDE:YoYoStudio.IDE::Initialise", "IDE:YoYoStudio.IDE::Bootstrap", "IDE:YoYoStudio.IDE::BootstrapSDL2",
         "IDE:YoYoStudio.IDE::Execute", "Utils:YoYoStudio.Core.Utils.MacroExpansion::Initialise"]
graph = collections.defaultdict(set)
for asm, caller, casm, callee, op in tsv("calls.tsv"):
    if op in ("Call", "Callvirt", "Newobj", "Ldftn", "Ldvirtftn"):
        graph[f"{asm}:{caller}"].add(f"{casm}:{callee}")
# metodos de tipo (.cctor) sao executados quando o tipo e tocado: liga Tipo::x -> Tipo::.cctor
types_with_cctor = {k.rsplit("::", 1)[0] for k in graph if k.endswith("::.cctor")}
reach = {}
stack = [(r, None) for r in ROOTS]
while stack:
    m, parent = stack.pop()
    if m in reach: continue
    reach[m] = parent
    a = m.split(":", 1)[0]
    if kinds.get(a) != "ide" or DESK(a): continue
    nxt = set(graph.get(m, ()))
    t = m.rsplit("::", 1)[0]
    for x in list(nxt):
        tt = x.rsplit("::", 1)[0]
        if tt in types_with_cctor: nxt.add(tt + "::.cctor")
    if t in types_with_cctor: nxt.add(t + "::.cctor")
    for n in nxt:
        if n not in reach: stack.append((n, m))
def path(m, n=6):
    p = []
    while m and len(p) < n: p.append(m); m = reach.get(m)
    return " <- ".join(p)

allrows = []; bootrows = []
for asm, ctype, cmeth, tasm, ttype, tmem, op in tsv("memberrefs.tsv"):
    if kinds.get(asm) != "ide" or DESK(asm): continue
    key = f"{tasm}|{ttype}::{tmem}"
    for rx, cat, eff in RULES:
        if rx.search(key):
            caller = f"{asm}:{ctype}::{cmeth}"
            row = (cat, asm, f"{ctype}::{cmeth}", f"{tasm}:{ttype}::{tmem}", eff)
            allrows.append(row)
            if caller in reach: bootrows.append(row + (path(caller),))
            break
with open(os.path.join(out, "managed-risk.tsv"), "w", encoding="utf-8") as f:
    f.write("category\tasm\tcaller\ttarget\teffect\n")
    for r in sorted(set(allrows)): f.write("\t".join(r) + "\n")
with open(os.path.join(out, "managed-risk-boot.tsv"), "w", encoding="utf-8") as f:
    f.write("category\tasm\tcaller\ttarget\teffect\tpath_from_root\n")
    for r in sorted(set(bootrows)): f.write("\t".join(r) + "\n")
with open(os.path.join(out, "boot-reachable.txt"), "w", encoding="utf-8") as f:
    for m in sorted(reach): f.write(m + "\n")
print("reachable", len(reach), "risk", len(set(allrows)), "boot", len(set(bootrows)))
c = collections.Counter(r[0] for r in set(allrows)); cb = collections.Counter(r[0] for r in set(bootrows))
for k, v in c.most_common(): print(f"{k:40s} all={v:5d} boot={cb.get(k,0)}")
