using System.IO.Compression;
using System.Text;
using GmIde;

// 1) extracao completa; 2) segunda passada com runtime ja instalado (so manifesto);
// 3) pacote adulterado (SHA-256 errado) nao deixa IDE.dll; 4) caminho "../" rejeitado.
if (args.Length is not (2 or 4)) { Console.WriteLine("uso: pkg-test <zip-ou-apk> <saida> [offset length]"); return 2; }
var zipPath = args[0];
var outDir = Path.GetFullPath(args[1]);
const string RtFolder = "runtime-2026.0.0.23";
int fails = 0;
Stream OpenPackage() => args.Length == 4
    ? new PackageAssetStream(File.OpenRead(zipPath), long.Parse(args[2]), long.Parse(args[3]))
    : File.OpenRead(zipPath);
void Check(bool ok, string what) { Console.WriteLine((ok ? "OK    " : "FALHA ") + what); if (!ok) fails++; }

PackageExtractor Make(string root) => new()
{
    IdeDir = Path.Combine(root, "files", "ide"),
    RuntimeDir = Path.Combine(PackageExtractor.SystemDirectoryFor(Path.Combine(root, "ext")), "Cache", "runtimes", RtFolder),
    PrefabsDir = Path.Combine(PackageExtractor.SystemDirectoryFor(Path.Combine(root, "ext")), "Prefabs"),
    RuntimeFolder = RtFolder,
    Log = Console.WriteLine,
};

if (Directory.Exists(outDir)) Directory.Delete(outDir, true);
var root1 = Path.Combine(outDir, "full");
var ex = Make(root1);
using (var fs = OpenPackage())
{
    using (var z = new ZipArchive(fs, ZipArchiveMode.Read, true))
        Console.WriteLine($"Bytes a gravar: {ex.BytesNeeded(z) / 1048576} MB");
    fs.Position = 0;
    ex.Extract(fs);
}
PackageInfo info;
using (var fs = OpenPackage())
using (var z = new ZipArchive(fs, ZipArchiveMode.Read)) info = PackageExtractor.ReadInfo(z)!;
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
// Confere todos os destinos contra o manifesto do pacote imutavel. A instalacao
// do PC pode receber modulos/updates depois da geracao do ZIP e nao e referencia
// para o conteudo desta distribuicao.
int diff = 0;
foreach (var (name, expected) in info.Files)
{
    string destination = name.StartsWith("ide/") ? Path.Combine(ex.IdeDir, name[4..])
        : name.StartsWith("runtime/Prefabs/") ? Path.Combine(ex.PrefabsDir, name[16..])
        : Path.Combine(ex.RuntimeDir, name[("runtime/" + RtFolder + "/").Length..]);
    if (!File.Exists(destination) || new FileInfo(destination).Length != expected.Size)
    {
        diff++;
        continue;
    }
    using var actual = File.OpenRead(destination);
    if (Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(actual)) != expected.Sha256)
        diff++;
}
Check(diff == 0, $"{info.Files.Count} arquivos extraidos com tamanho e SHA-256 do manifesto");

// 2) IDE apagada, runtime ja instalado: so reextrai a IDE e o manifesto do runtime.
Directory.Delete(ex.IdeDir, true);
var receiptTime = File.GetLastWriteTimeUtc(Path.Combine(ex.RuntimeDir, "receipt.json"));
File.WriteAllText(Path.Combine(ex.RuntimeDir, "gmide-runtime-integrity.json"), "velho");
var sentinel = Path.Combine(ex.RuntimeDir, "sentinela.txt");
File.WriteAllText(sentinel, "x");
using (var fs = OpenPackage()) ex.Extract(fs);
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

// O mesmo intervalo usado pelo AssetFileDescriptor: prefixo/sufixo nao vazam.
var backing = new MemoryStream(new byte[] { 99, 10, 20, 30, 88, 77 });
using (var slice = new PackageAssetStream(backing, 1, 3))
{
    var buffer = new byte[12];
    Check(slice.Read(buffer, 0, buffer.Length) == 3 && buffer.AsSpan(0, 3).SequenceEqual(new byte[] { 10, 20, 30 })
          && slice.ReadByte() == -1, "asset: leitura limitada ao intervalo, sem prefixo/sufixo");
    slice.Seek(-1, SeekOrigin.End);
    Check(slice.ReadByte() == 30 && slice.Position == 3, "asset: Seek(End) relativo ao pacote");
    bool outside = false;
    try { slice.Seek(1, SeekOrigin.End); } catch (IOException) { outside = true; }
    Check(outside, "asset: seek fora do intervalo rejeitado");
}
Check(!backing.CanRead, "asset: descritor fechado ao terminar");
Check(ex.RuntimeDir == Path.Combine(root1, "ext", "home", "GameMakerStudio2-LTS2026", "Cache", "runtimes", RtFolder),
    "bootstrap usa o system_directory observado na IDE Android");
var repaired = Path.Combine(outDir, "repaired-runtime");
Directory.CreateDirectory(repaired);
File.WriteAllText(Path.Combine(repaired, "receipt.json"), "linux-only");
File.WriteAllText(Path.Combine(repaired, "local-settings.txt"), "preservar");
Check(RuntimeIntegrity.RestoreFromLegacy(ex.RuntimeDir, repaired, Console.WriteLine) &&
    new[] { RuntimeIntegrity.IgorRel, RuntimeIntegrity.AssetCompilerRel, RuntimeIntegrity.WebServerRel }
        .All(rel => RuntimeIntegrity.VerifyFile(repaired, rel, null, out _)) &&
    File.ReadAllText(Path.Combine(repaired, "receipt.json")) == "linux-only" &&
    File.ReadAllText(Path.Combine(repaired, "local-settings.txt")) == "preservar",
    "recupera toolchain verificado mesmo com receipt Linux, preservando arquivos existentes");
Check(!RuntimeIntegrity.RestoreFromLegacy(ex.RuntimeDir, repaired, Console.WriteLine), "recuperacao idempotente");
File.WriteAllText(Path.Combine(ex.RuntimeDir, RuntimeIntegrity.ManifestName), "corrompido");
bool rejected = false;
var rejectedRuntime = Path.Combine(outDir, "rejected-runtime");
try { RuntimeIntegrity.RestoreFromLegacy(ex.RuntimeDir, rejectedRuntime, Console.WriteLine); }
catch (InvalidDataException) { rejected = true; }
Check(rejected && !Directory.Exists(rejectedRuntime), "origem invalida rejeitada antes de copiar");
Console.WriteLine(fails == 0 ? "TUDO OK" : $"{fails} FALHA(S)");
return fails == 0 ? 0 : 1;
