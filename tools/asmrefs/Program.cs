using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
var dir = args[0];
var fw = new HashSet<string>(Directory.GetFiles(args[1], "*.dll").Select(Path.GetFileNameWithoutExtension), StringComparer.OrdinalIgnoreCase);
bool IsFw(string n) => fw.Contains(n) || n.StartsWith("Presentation") || n is "WindowsBase" or "ReachFramework" or "DirectWriteForwarder" or "UIAutomationTypes" or "UIAutomationProvider" or "UIAutomationClient" or "System.Windows.Forms" or "System.Xaml" or "Accessibility";
var used = new SortedDictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
foreach (var f in Directory.GetFiles(dir, "*.dll"))
{
    var name = Path.GetFileNameWithoutExtension(f);
    if (IsFw(name)) continue;
    try {
        using var s = File.OpenRead(f); using var pe = new PEReader(s);
        if (!pe.HasMetadata) continue;
        var md = pe.GetMetadataReader();
        foreach (var h in md.AssemblyReferences) {
            var n = md.GetString(md.GetAssemblyReference(h).Name);
            if (!IsFw(n)) continue;
            if (!used.TryGetValue(n, out var u)) used[n] = u = new();
            u.Add(name);
        }
    } catch { }
}
foreach (var kv in used) Console.WriteLine($"{(fw.Contains(kv.Key) ? "core" : "DESK")} {kv.Key}\t{string.Join(",", kv.Value.Take(6))}{(kv.Value.Count>6?$" (+{kv.Value.Count-6})":"")}");
