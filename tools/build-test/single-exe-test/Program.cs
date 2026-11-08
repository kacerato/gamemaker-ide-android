using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using GmIde;

string preview = Path.GetFullPath(args[0]), player = Path.GetFullPath(args[1]);
string work = Path.GetFullPath(args[2]), reader = Path.GetFullPath(args[3]);
Directory.CreateDirectory(work);
if (args.Length > 4) { Console.WriteLine(await Read(Path.GetFullPath(args[4]))); return; }
string folder = WindowsWebPackage.Create(Path.Combine(work, Guid.NewGuid().ToString("N")), preview, player,
    "Single EXE Test", "com.gmide.singleexetest", "1.2.3.4", null, 960, 540);
File.WriteAllBytes(Path.Combine(folder, "empty"), []);
File.WriteAllBytes(Path.Combine(folder, "random.bin"), RandomNumberGenerator.GetBytes(180000));
File.WriteAllText(Path.Combine(folder, "acentuação.txt"), "UTF-8 e arquivos vazios.");
string alone = Path.Combine(work, "portable " + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(alone);
string exe = Path.Combine(alone, "Jogo.exe"); WindowsSingleFilePackage.Publish(folder, exe);
if (Directory.GetFiles(alone).Length != 1) throw new Exception("Output is not a single file.");
string cache = await Read(exe);
foreach (string f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".exe")))
    if (!SHA256.HashData(File.ReadAllBytes(f)).SequenceEqual(SHA256.HashData(File.ReadAllBytes(Path.Combine(cache, Path.GetRelativePath(folder, f))))))
        throw new Exception("Extracted bytes differ: " + f);
var before = File.GetLastWriteTimeUtc(Path.Combine(cache, "game.ini"));
await Task.WhenAll(Read(exe), Read(exe));
if (File.GetLastWriteTimeUtc(Path.Combine(cache, "game.ini")) != before) throw new Exception("Cache not reused.");
byte[] ini = File.ReadAllBytes(Path.Combine(cache, "game.ini")); ini[ini.Length - 1] ^= 1;
File.WriteAllBytes(Path.Combine(cache, "game.ini"), ini);
await Read(exe);
if (!File.ReadAllBytes(Path.Combine(cache, "game.ini")).SequenceEqual(File.ReadAllBytes(Path.Combine(folder, "game.ini"))))
    throw new Exception("Same-size corruption not repaired.");
byte[] original = File.ReadAllBytes(exe), damaged = original.ToArray();
long offset = (long)BinaryPrimitives.ReadUInt64LittleEndian(damaged.AsSpan(damaged.Length - 48));
damaged[(int)offset + 12] ^= 1;
string invalid = Path.Combine(work, "damaged.exe"); File.WriteAllBytes(invalid, damaged);
await Read(invalid, false);
// A valid payload checksum must not let a traversal filename pass the native reader.
damaged = original.ToArray(); byte[] marker = Encoding.UTF8.GetBytes("empty");
int at = damaged.AsSpan((int)offset).IndexOf(marker) + (int)offset;
Encoding.UTF8.GetBytes("../xx").CopyTo(damaged, at);
SHA256.HashData(damaged.AsSpan((int)offset, damaged.Length - 56 - (int)offset)).CopyTo(damaged, damaged.Length - 32);
File.WriteAllBytes(invalid, damaged); await Read(invalid, false);
byte[] goodHash = SHA256.HashData(original);
File.WriteAllText(Path.Combine(folder, "CON.txt"), "invalid");
try { WindowsSingleFilePackage.Publish(folder, exe); throw new Exception("Reserved filename accepted."); }
catch (InvalidDataException) { }
if (!SHA256.HashData(File.ReadAllBytes(exe)).SequenceEqual(goodHash)) throw new Exception("Failed export replaced good EXE.");
Console.WriteLine("PASS: lone EXE, real game assets, streaming extraction, Unicode/empty files, concurrent cache reuse, corruption repair, checksum/traversal rejection, atomic publication.");
Console.WriteLine("PLAYER=" + exe); Console.WriteLine("CACHE=" + cache);

async Task<string> Read(string file, bool expected = true)
{
    var start = new ProcessStartInfo(reader) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    start.ArgumentList.Add(file);
    using var p = Process.Start(start)!;
    var stdout = p.StandardOutput.ReadToEndAsync(); var stderr = p.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    try { await p.WaitForExitAsync(timeout.Token); }
    catch { p.Kill(true); throw; }
    string output = (await stdout).Trim(), error = (await stderr).Trim();
    if ((p.ExitCode == 0) != expected) throw new Exception($"Native reader exit={p.ExitCode}: {error}");
    if (expected && output.Length == 0) throw new Exception("No extracted path.");
    return output;
}
