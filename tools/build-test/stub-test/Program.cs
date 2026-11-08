// Teste do caminho stub -> bridge -> Igor em processo separado (docs/build-pipeline.md, secao 2).
//
//   stub-test server <runtime> <cacheDir> <socket> <manifest> <stub>
//       Processo "build" (papel do BuildService :build): BuildBridge.StartServer num socket unix
//       de arquivo. Se o stub de um Igor cair no meio, sai com 3 (no Android: KillProcess).
//   stub-test e2e <runtime> <cacheDir> <manifest> <gmexec.exe> [--kill-after-ms=N] -- <args do Igor>
//       Papel da IDE: sobe o servidor acima em OUTRO processo, lanca gmexec.exe como o
//       CmdProcess (Process.Start, stdout/stderr redirecionados, BeginOutputReadLine) e
//       confere exit code e "Igor complete.".
//   stub-test verify <runtime> <manifest> <stub>
//       Casos do substituto do WinTrust.VerifySecurity (BuildBridge.VerifyBuildTools).
using System.Diagnostics;
using GmIde;

switch (args.Length > 0 ? args[0] : "")
{
    case "server": return Server(args[1], args[2], args[3], args[4], args[5]);
    case "e2e": return E2E(args);
    case "verify": return Verify(args[1], args[2], args[3]);
    default: Console.Error.WriteLine("uso: stub-test server|e2e|verify ..."); return 2;
}

static int Server(string runtime, string cache, string socket, string manifest, string stub)
{
    RuntimeIntegrity.ManifestOverride = manifest;
    BuildBridge.SocketEndpoint = socket;
    BuildBridge.Logger = s => Console.Error.WriteLine("[server " + Environment.ProcessId + "] " + s);
    BuildBridge.IgorClientAborted += () => { Console.Error.WriteLine("[server] stub caiu: encerrando o processo de build"); Environment.Exit(3); };
    BuildBridge.OpenUrlRequested += u => Console.Error.WriteLine("[server] OpenUrlRequested: " + u);
    BuildBridge.ConfigureInBackground(runtime, Path.GetFullPath(stub), Path.GetFullPath(cache));
    BuildBridge.StartServer();
    Thread.Sleep(Timeout.Infinite);
    return 0;
}

static int E2E(string[] a)
{
    string runtime = a[1], cache = a[2], manifest = a[3], stub = Path.GetFullPath(a[4]);
    int killAfter = 0;
    int sep = Array.IndexOf(a, "--");
    foreach (var o in a.Take(sep).Skip(5))
        if (o.StartsWith("--kill-after-ms=")) killAfter = int.Parse(o.Substring(16));
    var igorArgs = string.Join(" ", a.Skip(sep + 1).Select(x => x.Contains(' ') && !x.Contains('"') ? "\"" + x + "\"" : x));
    var socket = Path.Combine(Path.GetTempPath(), "gmexec-test-" + Environment.ProcessId + ".sock");

    // 1) processo de build separado
    var self = Environment.ProcessPath!;
    var srvInfo = new ProcessStartInfo(self) { UseShellExecute = false, RedirectStandardError = true };
    if (self.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase)) srvInfo.ArgumentList.Add(typeof(BuildBridge).Assembly.Location);
    foreach (var x in new[] { "server", runtime, cache, socket, manifest, stub }) srvInfo.ArgumentList.Add(x);
    using var server = Process.Start(srvInfo)!;
    server.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine("    " + e.Data); };
    server.BeginErrorReadLine();
    Console.WriteLine($"[ide] processo de build: pid {server.Id}");

    // 2) "IDE": CmdProcess -> Process.Start(${igor_path}=stub)
    var sw = Stopwatch.StartNew();
    var psi = new ProcessStartInfo(stub, igorArgs)
    {
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true,
        StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
    };
    psi.Environment["GMEXEC_SOCKET"] = socket;
    psi.Environment["GMAssetLogLoc"] = Path.Combine(cache, "aclog");
    Console.WriteLine($"[ide] {stub} {igorArgs}");
    using var p = Process.Start(psi)!;
    Console.WriteLine($"[ide] stub: pid {p.Id}");
    bool done = false; int lines = 0;
    p.OutputDataReceived += (_, e) => { if (e.Data == null) return; lines++; if (e.Data == BuildBridge.IgorDonePhrase) done = true; Console.WriteLine("O| " + e.Data); };
    p.ErrorDataReceived += (_, e) => { if (e.Data != null) Console.WriteLine("E| " + e.Data); };
    p.BeginOutputReadLine(); p.BeginErrorReadLine();
    if (killAfter > 0 && !p.WaitForExit(killAfter))
    {
        Console.WriteLine($"[ide] Stop: matando o stub depois de {killAfter} ms (CmdProcess.Terminate)");
        p.Kill();
        p.WaitForExit();
        bool srvDied = server.WaitForExit(10000);
        Console.WriteLine($"[ide] processo de build {(srvDied ? "encerrou com " + server.ExitCode : "AINDA VIVO")}");
        return srvDied && server.ExitCode == 3 ? 0 : 1;
    }
    p.WaitForExit();
    Console.WriteLine($"[ide] stub exit={p.ExitCode} em {sw.Elapsed.TotalSeconds:F1}s, {lines} linhas, \"Igor complete.\"={(done ? "sim" : "nao")}");
    Console.WriteLine($"[ide] processo de build ainda vivo depois do build: {!server.HasExited}");
    try { server.Kill(); } catch { }
    return p.ExitCode == 0 && done ? 0 : 1;
}

static int Verify(string runtime, string manifest, string stub)
{
    BuildBridge.Logger = s => Console.WriteLine("    log: " + s);
    var orgs = new[] { "YoYo Games Ltd", "YOYO GAMES LIMITED" };
    int fails = 0;
    void Case(string name, bool expected, Func<bool> f)
    {
        bool got = f();
        Console.WriteLine($"{(got == expected ? "ok  " : "FAIL")} {name}: {got} (esperado {expected})");
        if (got != expected) fails++;
    }
    stub = Path.GetFullPath(stub);
    BuildBridge.ConfigureClient(runtime, stub);
    RuntimeIntegrity.ManifestOverride = manifest;
    Case("stub + Igor.exe conferido", true, () => BuildBridge.VerifyBuildTools(stub, orgs));
    Case("outro executavel no lugar do stub", false, () => BuildBridge.VerifyBuildTools(Path.Combine(runtime, RuntimeIntegrity.IgorRel), orgs));
    Case("orgs sem a YoYo", false, () => BuildBridge.VerifyBuildTools(stub, new[] { "Outra Ltd" }));
    var tmp = Path.Combine(Path.GetTempPath(), "gmexec-verify-" + Environment.ProcessId);
    Directory.CreateDirectory(tmp);
    var bad = Path.Combine(tmp, "manifest-bad.json");
    File.WriteAllText(bad, File.ReadAllText(manifest).Replace("301EA5BB", "00000000"));
    RuntimeIntegrity.ManifestOverride = bad;
    Case("hash do Igor.exe diferente do manifesto", false, () => BuildBridge.VerifyBuildTools(stub, orgs));
    RuntimeIntegrity.ManifestOverride = Path.Combine(tmp, "nao-existe.json");
    Case("sem manifesto", false, () => BuildBridge.VerifyBuildTools(stub, orgs));
    RuntimeIntegrity.ManifestOverride = manifest;
    BuildBridge.ActiveRuntimeDir = () => tmp; // runtime ativo sem Igor.exe
    Case("runtime ativo sem Igor.exe", false, () => BuildBridge.VerifyBuildTools(stub, orgs));
    Directory.Delete(tmp, true);
    return fails == 0 ? 0 : 1;
}
