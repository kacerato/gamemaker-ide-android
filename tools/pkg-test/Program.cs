using System.IO.Compression;
using System.Text;
using GmIde;

// 1) extracao completa; 2) segunda passada com runtime ja instalado (so manifesto);
// 3) pacote adulterado (SHA-256 errado) nao deixa IDE.dll; 4) caminho "../" rejeitado.
if (args.Length < 2) { Console.WriteLine("uso: pkg-test <gmide-package.zip> <saida>"); return 2; }
var zipPath = args[0];
var outDir = Path.GetFullPath(args[1]);
const string RtFolder = "runtime-2026.0.0.23";
int fails = 0;
void Check(bool ok, string what) { Console.WriteLine((ok ? "OK    " : "FALHA ") + what); if (!ok) fails++; }

PackageExtractor Make(string root) => new()
{
    IdeDir = Path.Combine(root, "files", "ide"),
    RuntimeDir = Path.Combine(root, "ext", "home", ".local", "share", "GameMakerStudio2-LTS2026", "Cache", "runtimes", RtFolder),
    PrefabsDir = Path.Combine(root, "ext", "home", ".local", "share", "GameMakerStudio2-LTS2026", "Prefabs"),
    RuntimeFolder = RtFolder,
    Log = Console.WriteLine,
};

if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
var root1 = Path.Combine(outDir, "full");
var ex = Make(root1);
using (var fs = File.OpenRead(zipPath))
{
    using (var z = new ZipArchive(fs, ZipArchiveMode.Read, true))
        Console.WriteLine($"Bytes a gravar: {ex.BytesNeeded(z) / 1048576} MB");
    fs.Position = 0;
    ex.Extract(fs);
}
PackageInfo info;
using (var z = ZipFile.OpenRead(zipPath)) info = PackageExtractor.ReadInfo(z)!;
Check(File.Exists(Path.Combine(ex.IdeDir, "IDE.dll")), "ide/IDE.dll");
Check(File.Exists(Path.Combine(ex.IdeDir, "ide-integrity.json")), "ide/ide-integrity.json");
Check(File.Exists(Path.Combine(ex.RuntimeDir, "receipt.json")), "runtime receipt.json");
Check(File.Exists(Path.Combine(ex.RuntimeDir, "gmide-runtime-integrity.json")), "runtime manifesto");
Check(Directory.Exists(ex.PrefabsDir), "Prefabs");
Check(!Directory.EnumerateDirectories(outDir, "*.importando", SearchOption.AllDirectories).Any(), "sem pastas .importando");
int count = Directory.EnumerateFiles(ex.IdeDir, "*", SearchOption.AllDirectories).Count()
          + Directory.EnumerateFiles(ex.RuntimeDir, "*", SearchOption.AllDirectories).Count()
          + Directory.EnumerateFiles(ex.PrefabsDir, "*", SearchOption.AllDirectories).Count();
Check(count == info.Files.Count, $"arquivos extraidos {count} == package.json {info.Files.Count}");
// Confere contra as fontes originais do PC (tamanho de todos + bytes de alguns).
var srcs = new (string Src, string Dst)[] {
    (@"C:\Program Files\GameMaker-LTS2026", ex.IdeDir),
    (@"C:\ProgramData\GameMakerStudio2-LTS2026\Cache\runtimes\" + RtFolder, ex.RuntimeDir),
    (@"C:\ProgramData\GameMakerStudio2-LTS2026\Prefabs", ex.PrefabsDir) };
int diff = 0, compared = 0;
foreach (var (src, dst) in srcs)
    foreach (var f in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
    {
        var rel = Path.GetRelativePath(src, f);
        if (rel is "ide-integrity.json" or "gmide-runtime-integrity.json" or ".gmide-complete") continue;
        var d = Path.Combine(dst, rel);
        compared++;
        if (!File.Exists(d) || new FileInfo(d).Length != new FileInfo(f).Length) { diff++; if (diff < 5) Console.WriteLine("  diferente: " + rel); }
    }
Check(diff == 0, $"{compared} arquivos com mesmo tamanho das fontes");
foreach (var rel in new[] { "IDE.dll", "OSCore.dll", "Utils.dll" })
    Check(File.ReadAllBytes(Path.Combine(srcs[0].Src, rel)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(ex.IdeDir, rel))), "bytes iguais: " + rel);

// 2) IDE apagada, runtime ja instalado: so reextrai a IDE e o manifesto do runtime.
Directory.Delete(ex.IdeDir, true);
var receiptTime = File.GetLastWriteTimeUtc(Path.Combine(ex.RuntimeDir, "receipt.json"));
File.WriteAllText(Path.Combine(ex.RuntimeDir, "gmide-runtime-integrity.json"), "velho");
var sentinel = Path.Combine(ex.RuntimeDir, "sentinela.txt");
File.WriteAllText(sentinel, "x");
using (var fs = File.OpenRead(zipPath)) ex.Extract(fs);
Check(File.Exists(Path.Combine(ex.IdeDir, "IDE.dll")), "2a passada: IDE.dll");
Check(File.Exists(sentinel), "2a passada: runtime existente preservado (nao reextraido)");
Check(File.ReadAllText(Path.Combine(ex.RuntimeDir, "gmide-runtime-integrity.json")) != "velho", "2a passada: manifesto do runtime atualizado");

// 3) e 4) pacotes pequenos sinteticos.
string MiniZip(string name, Action<ZipArchive> fill)
{
    var p = Path.Combine(outDir, name);
    using var z = ZipFile.Open(p, ZipArchiveMode.Create);
    fill(z);
    return p;
}
void Put(ZipArchive z, string n, string text)
{
    using var w = new StreamWriter(z.CreateEntry(n).Open(), new UTF8Encoding(false));
    w.Write(text);
}
var bad = MiniZip("adulterado.zip", z =>
{
    Put(z, "ide/IDE.dll", "conteudo trocado");
    Put(z, "package.json", "{\"format\":1,\"files\":{\"ide/IDE.dll\":{\"sha256\":\"" + new string('0', 64) + "\",\"size\":1}}}");
});
var exBad = Make(Path.Combine(outDir, "bad"));
bool threw = false;
try { using var fs = File.OpenRead(bad); exBad.Extract(fs); } catch (InvalidDataException e) { threw = true; Console.WriteLine("  esperado: " + e.Message); }
Check(threw && !File.Exists(Path.Combine(exBad.IdeDir, "IDE.dll")), "SHA-256 errado: falha sem IDE.dll no destino");

var slip = MiniZip("slip.zip", z => { Put(z, "ide/IDE.dll", "x"); Put(z, "ide/../../fora.txt", "x"); });
var exSlip = Make(Path.Combine(outDir, "slip"));
threw = false;
try { using var fs = File.OpenRead(slip); exSlip.Extract(fs); } catch (InvalidDataException e) { threw = true; Console.WriteLine("  esperado: " + e.Message); }
Check(threw && !File.Exists(Path.Combine(outDir, "slip", "fora.txt")) && !File.Exists(Path.Combine(exSlip.IdeDir, "IDE.dll")), "caminho '..' rejeitado");

Console.WriteLine(fails == 0 ? "TUDO OK" : $"{fails} FALHA(S)");
return fails == 0 ? 0 : 1;
