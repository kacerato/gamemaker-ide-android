using System.Runtime.InteropServices;
using Android.App;
using Android.Runtime;

namespace GmIde;

// Roda antes da Activity: registra o lancador C# como SDL_main.
[Application(Name = "io.gmide.MainApplication")]
public class MainApplication : Application
{
    public static MainApplication Instance { get; private set; } = null!;

    public MainApplication(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    public override unsafe void OnCreate()
    {
        base.OnCreate();
        Instance = this;
        string processName = File.ReadAllText("/proc/self/cmdline").TrimEnd('\0');
        // Antes de tudo: crash em qualquer etapa seguinte ja fica registrado (docs/telemetria.md).
        Telemetry.Init(this, processName);
        if (processName.EndsWith(":game", StringComparison.Ordinal))
        {
            IntPtr cls = JNIEnv.FindClass("com/yoyogames/runner/RunnerJNILib");
            try { JNIEnv.CallStaticVoidMethod(cls, JNIEnv.GetStaticMethodID(cls, "Init", "()V")); }
            finally { JNIEnv.DeleteGlobalRef(cls); }
            return;
        }
        gmhost_set_main(&Launcher.SdlMain);
    }

    [DllImport("gmhost")]
    static extern unsafe void gmhost_set_main(delegate* unmanaged<int, IntPtr, int> fn);
}
