using System.Text.Json;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Util;

namespace GmIde;

// Processo separado para o Igor (docs/build-pipeline.md, secao 3a): o Igor e o
// GMAssetCompiler chamam Environment.Exit em qualquer erro (inclusive erros de GML) e mudam
// Environment.CurrentDirectory. Num processo proprio isso so derruba o servico; a IDE recebe
// o codigo de saida pelo stub (BuildBridge.OnProcessExit) e o Android recria o servico na
// proxima ligacao (BuildIntegration religa ao perceber a queda).
//
// O MainApplication.OnCreate tambem roda aqui (registra o SDL_main, que nunca e chamado
// neste processo). A IDE (outro processo) grava a configuracao em files/build-bridge.json.
[Service(Name = "io.gmide.BuildService", Process = ":build", Exported = false)]
public class BuildService : Service
{
    const string Tag = "GmIde.Build";
    public const string ActionStopRun = "io.gmide.action.STOP_RUN";

    public override void OnCreate()
    {
        base.OnCreate();
        try
        {
            var cfg = BuildIntegration.ReadServiceConfig(this);
            if (cfg == null)
            {
                Log.Error(Tag, "BuildService: configuracao ausente (a IDE nao chamou BuildIntegration.Configure)");
                return;
            }
            BuildBridge.IgorClientAborted += KillSelf;
            BuildBridge.OpenUrlRequested += OpenRunner;
            BuildBridge.ConfigureInBackground(cfg.RuntimeDir, cfg.StubPath, cfg.CacheDir);
            ToolIntegration.ConfigureService(this); // ProjectTool / Language Server (ToolBridge.cs)
            BuildBridge.StartServer();
            Log.Info(Tag, $"BuildService pronto no processo {Process.MyPid()}");
        }
        catch (Exception e)
        {
            Log.Error(Tag, "BuildService.OnCreate: " + e);
        }
    }

    public override IBinder? OnBind(Intent? intent) => new Binder();

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == ActionStopRun)
        {
            // O jogo foi fechado no WebView: termina o "OperaGX Run" (Igor esperando o webserver)
            // como sucesso e recomeca o processo limpo.
            Log.Info(Tag, "Run encerrado pelo WebView");
            BuildBridge.AbortAll(0, "");
            new Thread(() => { Thread.Sleep(300); KillSelf(); }) { IsBackground = true }.Start();
        }
        return StartCommandResult.NotSticky;
    }

    static void KillSelf()
    {
        LocalGameRun.Stop();
        Log.Warn(Tag, "Encerrando o processo de build");
        Process.KillProcess(Process.MyPid());
    }

    // "Attempting to open browser ... with URL "http://localhost:<porta>/runner.html"" do Igor.
    void OpenRunner(string url)
    {
        new Thread(() =>
        {
            try
            {
                // O Igor lanca o webserver (outro Dispatch) e logo imprime a URL; espera o -root chegar.
                for (int i = 0; i < 30 && BuildBridge.LastWebRoot == null; i++) Thread.Sleep(100);
                var intent = new Intent();
                intent.SetClassName(PackageName!, "io.gmide.GameRunnerActivity");
                intent.PutExtra("url", url);
                if (BuildBridge.LastWebRoot != null) intent.PutExtra("root", BuildBridge.LastWebRoot);
                intent.AddFlags(ActivityFlags.NewTask);
                StartActivity(intent);
                Log.Info(Tag, $"Runner GX: {url} (root {BuildBridge.LastWebRoot})");
            }
            catch (Exception e)
            {
                Log.Error(Tag, "Abrir o runner falhou: " + e);
            }
        }) { IsBackground = true, Name = "gmide-open-runner" }.Start();
    }
}
