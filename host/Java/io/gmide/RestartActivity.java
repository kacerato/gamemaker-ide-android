package io.gmide;

import android.app.Activity;
import android.content.Intent;
import android.os.Bundle;
import android.util.Log;

import java.io.File;

/**
 * Reinicio da IDE (PlatformBridge.SpawnNewIde): roda no processo ":restart", espera o processo da IDE
 * (extra "pid") terminar e abre a GmIdeActivity de novo; depois encerra o proprio processo.
 * Sem interface (tema translucido).
 */
public class RestartActivity extends Activity {
    private static final String TAG = "GmIde.Restart";

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        final int pid = getIntent().getIntExtra("pid", -1);
        final Activity self = this;
        new Thread(new Runnable() {
            public void run() {
                long deadline = System.currentTimeMillis() + 15000;
                while (pid > 0 && new File("/proc/" + pid).exists() && System.currentTimeMillis() < deadline) {
                    try { Thread.sleep(100); } catch (InterruptedException e) { break; }
                }
                Log.i(TAG, "processo da IDE " + pid + (new File("/proc/" + pid).exists() ? " ainda vivo (tempo esgotado)" : " terminou") + "; reabrindo");
                Intent i = new Intent();
                i.setClassName(getPackageName(), "io.gmide.GmIdeActivity");
                i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TASK);
                try { self.startActivity(i); } catch (Exception e) { Log.e(TAG, "reabrir: " + e); }
                self.finish();
                android.os.Process.killProcess(android.os.Process.myPid());
            }
        }).start();
    }
}
