package io.gmide;

import android.app.Activity;
import android.app.PendingIntent;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageInstaller;
import android.os.Build;
import android.os.Bundle;
import android.util.Log;
import android.widget.Toast;

import java.io.File;
import java.io.FileInputStream;
import java.io.InputStream;
import java.io.OutputStream;

/**
 * Instala o APK gerado pelo "Gerar APK" com PackageInstaller (sem FileProvider). Chamado pelo
 * C# do processo :build (ApkBridge.cs) logo depois de montar o APK. O Android mostra a
 * confirmacao: o status volta para ApkInstaller.Status (activity transparente), que abre a
 * tela de confirmacao do sistema ou mostra o resultado.
 * Requer REQUEST_INSTALL_PACKAGES e "instalar apps desconhecidos" liberado para este app.
 */
public final class ApkInstaller {
    static final String TAG = "GmIde.Apk";

    private ApkInstaller() { }

    /** Devolve null se a sessao foi enviada; senao a mensagem de erro. */
    public static String install(Context ctx, String apkPath) {
        File f = new File(apkPath);
        if (!f.isFile()) return "arquivo nao existe: " + apkPath;
        PackageInstaller pi = ctx.getPackageManager().getPackageInstaller();
        PackageInstaller.Session s = null;
        try {
            PackageInstaller.SessionParams p = new PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL);
            p.setSize(f.length());
            if (Build.VERSION.SDK_INT >= 31) p.setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_REQUIRED);
            int id = pi.createSession(p);
            s = pi.openSession(id);
            InputStream in = new FileInputStream(f);
            OutputStream out = s.openWrite("base.apk", 0, f.length());
            byte[] buf = new byte[1 << 16];
            int n;
            while ((n = in.read(buf)) > 0) out.write(buf, 0, n);
            s.fsync(out);
            out.close();
            in.close();
            Intent i = new Intent(ctx, Status.class);
            i.putExtra("apk", apkPath);
            int flags = PendingIntent.FLAG_UPDATE_CURRENT | (Build.VERSION.SDK_INT >= 31 ? PendingIntent.FLAG_MUTABLE : 0);
            PendingIntent cb = PendingIntent.getActivity(ctx, id, i, flags);
            s.commit(cb.getIntentSender());
            Log.i(TAG, "Sessao de instalacao " + id + " enviada: " + apkPath);
            return null;
        } catch (Throwable t) {
            Log.e(TAG, "instalar " + apkPath, t);
            if (s != null) try { s.abandon(); } catch (Throwable ignored) { }
            return t.toString();
        }
    }

    /** Recebe o status do PackageInstaller (processo principal). */
    public static class Status extends Activity {
        @Override
        protected void onCreate(Bundle b) {
            super.onCreate(b);
            handle(getIntent());
            finish();
        }

        void handle(Intent intent) {
            int status = intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE);
            String msg = intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE);
            String pkg = intent.getStringExtra(PackageInstaller.EXTRA_PACKAGE_NAME);
            Log.i(TAG, "Status da instalacao: " + status + " " + msg + " (" + pkg + ")");
            if (status == PackageInstaller.STATUS_PENDING_USER_ACTION) {
                Intent confirm = (Intent) intent.getParcelableExtra(Intent.EXTRA_INTENT);
                if (confirm != null) {
                    confirm.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
                    try { startActivity(confirm); } catch (Throwable t) { toast("Nao consegui abrir a confirmacao: " + t); }
                }
            } else if (status == PackageInstaller.STATUS_SUCCESS) {
                toast("Jogo instalado" + (pkg != null ? ": " + pkg : ""));
            } else if (status == PackageInstaller.STATUS_FAILURE_CONFLICT) {
                toast("Instalacao recusada: ja existe um app com esse pacote assinado com outra chave. Desinstale-o ou mude o pacote.");
            } else if (status == PackageInstaller.STATUS_FAILURE_ABORTED) {
                toast("Instalacao cancelada");
            } else {
                toast("Instalacao falhou (" + status + "): " + msg);
            }
        }

        void toast(String s) { Toast.makeText(getApplicationContext(), s, Toast.LENGTH_LONG).show(); }
    }
}
