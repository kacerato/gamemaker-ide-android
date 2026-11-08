// Uso: bridge-harness <runtimeDir> <cacheDir> <args do Igor...>
// Ex.: bridge-harness C:\ProgramData\...\runtime-2026.0.0.23 ..\bridge-cache -j=8 -options="..\temp\build.bff" -v -- Windows Compile
using GmIde;
var rt = args[0]; var cache = Path.GetFullPath(args[1]);
// Manifesto de assinaturas (tools/make-runtime-manifest.ps1) fora da pasta do runtime do usuario.
RuntimeIntegrity.ManifestOverride = Path.Combine(AppContext.BaseDirectory, "..", "stub-test", "gmide-runtime-integrity.json");
BuildBridge.Configure(rt, execStubPath: "", cacheDir: cache);
BuildBridge.OpenUrlRequested += u => Console.WriteLine("[harness] OpenUrlRequested: " + u);
var igorArgs = args.Skip(2).ToArray();
var sw = System.Diagnostics.Stopwatch.StartNew();
int code = BuildBridge.Dispatch(igorArgs, Environment.CurrentDirectory, new Dictionary<string, string>(),
    l => Console.WriteLine("O| " + l), l => Console.WriteLine("E| " + l));
Console.WriteLine($"[harness] exit={code} em {sw.Elapsed.TotalSeconds:F1}s");
return code;
