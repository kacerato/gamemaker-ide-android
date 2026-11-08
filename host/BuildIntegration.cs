using System.Text.Json;
using Android.Content;
using Android.OS;
using Android.Util;

namespace GmIde;

// Liga o Build/Run da IDE ao stub + servico :build (docs/build-pipeline.md, secao 4).
// Unico ponto de entrada para o Launcher: BuildIntegration.Configure(ideDir), no BootIde,
// depois de MacroExpansion.Initialise e antes de YoYoStudio.IDE.Bootstrap():
//  1. RuntimeImport: runtime-2026.0.0.23 + Prefabs no lugar que a IDE espera (runtime.json).
//  2. ${igor_path}/${webserver_path} -> <nativeLibraryDir>/libgmexec.so (BuildBridge.PointIdeAtStub).
//     Definido antes do RegisterDefaultMacros; no fim da splash, RuntimeEntry.UpdateDefaultRuntimeToolPaths
//     mantem o valor porque o arquivo existe.
//  3. Grava files/build-bridge.json e liga (bind) o BuildService, que sobe no processo :build.
//  4. VerifyBuildTools (alvo do patch de WinTrust.VerifySecurity, BuildPatches.cs) passa a
//     conferir o Igor.exe do runtime ativo (${runtimeLocation}) contra o manifesto do PC.
public static class BuildIntegration
{
    const string Tag = "GmIde.Build";
    public const string StubName = "libgmexec.so";
    const string ConfigName = "build-bridge.json";

    public sealed record ServiceConfig(string RuntimeDir, string StubPath, string CacheDir);

    static Connection? _conn;

    public static void EnsureBuildService()
    {
        if (_conn?.Connected != true) BindBuildService();
    }

    public static void Configure(string ideDir)
    {
        // Seletor de alvo: layout padrao achavel + alvo nunca nulo (BuildTarget.cs). Independe do stub.
        try { BuildTarget.Configure(ideDir); }
        catch (Exception e) { Log.Error(Tag, "BuildTarget.Configure: " + e); }
        try
        {
            var app = MainApplication.Instance;
            var stub = Path.Combine(app.ApplicationInfo!.NativeLibraryDir!, StubName);
            if (!File.Exists(stub))
            {
                Log.Error(Tag, $"{stub} nao existe: falta o stub no APK (bash native/gmexec/build.sh + AndroidNativeLibrary). Run/Build desligados.");
                return;
            }
            var runtimeDir = RuntimeImport.Run() ?? RuntimeImport.DefaultRuntimeDir();
            // A IDE so aceita ${webserver_path} chamado GMWebServer(.exe): link para o stub.
            try
            {
                var bin = Path.Combine(app.FilesDir!.AbsolutePath, "bin");
                Directory.CreateDirectory(bin);
                // A IDE confere o nome do arquivo: GMWebServer(.exe) e Igor(.exe).
                string Link(string name)
                {
                    var link = Path.Combine(bin, name);
                    // Apos reinstalar, o APK muda de pasta e o link antigo fica quebrado:
                    // File.Exists da false para link quebrado, entao apaga sempre.
                    try { global::Android.Systems.Os.Remove(link); } catch (global::Android.Systems.ErrnoException) { }
                    global::Android.Systems.Os.Symlink(stub, link);
                    return link;
                }
                BuildBridge.WebServerPath = Link("GMWebServer");
                BuildBridge.IgorPath = Link("Igor");
                BuildBridge.FixUserSettings(BuildBridge.WebServerPath);
            }
            catch (Exception e) { Log.Warn(Tag, "Link GMWebServer: " + e.Message); }
            BuildBridge.ConfigureClient(runtimeDir, stub);
            BuildBridge.ActiveRuntimeDir = CurrentRuntimeLocation;

            var cfg = new ServiceConfig(runtimeDir, stub, Path.Combine(app.NoBackupFilesDir!.AbsolutePath, "build-tools"));
            File.WriteAllText(Path.Combine(app.FilesDir!.AbsolutePath, ConfigName), JsonSerializer.Serialize(cfg));

            BuildBridge.PointIdeAtStub();
            ToolIntegration.Configure(ideDir, stub); // ProjectTool e Language Server pelo mesmo stub (docs/external-tools.md)
            BindBuildService();
            Log.Info(Tag, $"Build/Run: stub {stub}, runtime {runtimeDir}");
        }
        catch (Exception e)
        {
            Log.Error(Tag, "BuildIntegration.Configure: " + e);
        }
    }

    internal static ServiceConfig? ReadServiceConfig(Context ctx)
    {
        var path = Path.Combine(ctx.FilesDir!.AbsolutePath, ConfigName);
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<ServiceConfig>(File.ReadAllText(path));
    }

    // ${runtimeLocation} so vale depois que a IDE leu runtime.json; antes disso e o padrao
    // "C:\Temp\YoYoStudio\runtime" (DefaultMacros) - nesse caso usa o runtime importado.
    static string? CurrentRuntimeLocation()
    {
        try
        {
            var s = YoYoStudio.Core.Utils.MacroExpansion.Expand("${runtimeLocation}");
            return string.IsNullOrEmpty(s) || s.Contains("${") || !Directory.Exists(s) ? null : s;
        }
        catch { return null; }
    }

    // BIND_AUTO_CREATE: o servico sobe agora e volta se o processo :build morrer.
    // Uma ligacao NOVA cancela a espera (backoff) que o Android aplica a servicos que caem
    // seguidamente - por isso, ao cair, religamos com outro ServiceConnection. O stub espera
    // ate 30 s pelo socket enquanto isso.
    static void BindBuildService()
    {
        var app = MainApplication.Instance;
        var old = _conn;
        if (old != null)
        {
            try { app.UnbindService(old); } catch (Exception e) { Log.Warn(Tag, "unbind: " + e.Message); }
        }
        _conn = new Connection();
        var intent = new Intent(app, typeof(BuildService));
        if (!app.BindService(intent, _conn, Bind.AutoCreate | Bind.Important))
            Log.Error(Tag, "bindService(BuildService) recusado");
    }

    sealed class Connection : Java.Lang.Object, IServiceConnection
    {
        public volatile bool Connected;
        public void OnServiceConnected(ComponentName? name, IBinder? service)
        {
            Connected = true;
            Log.Info(Tag, "BuildService conectado");
        }

        public void OnServiceDisconnected(ComponentName? name)
        {
            if (this != _conn) return;
            Connected = false;
            Log.Warn(Tag, "Processo :build caiu (Environment.Exit do Igor, Stop ou fim do Run); religando");
            // Disconnection can precede actual process death. Rebinding immediately can
            // attach to that dying service and leave the next stub waiting on a dead socket.
            new Handler(Looper.MainLooper!).PostDelayed(() => { if (this == _conn) BindBuildService(); }, 500);
        }
    }
}
