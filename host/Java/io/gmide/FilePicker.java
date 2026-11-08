package io.gmide;

import android.app.Activity;
import android.content.Intent;
import android.os.Looper;
import android.util.Log;

import org.libsdl.app.SDLActivity;

import java.util.concurrent.CountDownLatch;

// Ponte sincrona para startActivityForResult, usada pelos dialogos de arquivo
// em C# (host/AndroidFileDialogs.cs).
//
// O C# chama startAndWait() a partir da thread do SDL (a thread da IDE). O
// metodo agenda startActivityForResult na thread de UI e BLOQUEIA a thread do
// SDL ate o resultado chegar em GmIdeActivity.onActivityResult -> deliver().
// Assim o ShowDialog continua sincrono, como o OpenFileDialog_Linux original.
//
// Sem lambdas/method references: o javac desta toolchain nao aceita.
public final class FilePicker {
    private static final String TAG = "GmIde";
    // Faixa propria para nao colidir com outros requestCodes do SDL.
    static final int REQUEST_CODE = 0x6D1D;

    private static final Object sLock = new Object();
    private static CountDownLatch sLatch;
    private static int sResultCode;
    private static Intent sResultData;

    private FilePicker() { }

    /**
     * Abre a Activity do intent e espera o resultado.
     * Retorna o Intent de resposta se resultCode == RESULT_OK (ou se acceptAnyResult),
     * e null em cancelamento/erro. Quando acceptAnyResult e true e nao ha Intent de
     * resposta, devolve um Intent vazio (usado pela tela de permissao, que nao
     * retorna dados).
     */
    public static Intent startAndWait(final Intent request, boolean acceptAnyResult) {
        if (Looper.myLooper() == Looper.getMainLooper()) {
            Log.e(TAG, "FilePicker.startAndWait chamado na thread de UI (deadlock evitado)");
            return null;
        }
        final Activity activity = (Activity) SDLActivity.getContext();
        if (activity == null || activity.isFinishing()) {
            Log.e(TAG, "FilePicker: Activity indisponivel");
            return null;
        }

        final CountDownLatch latch = new CountDownLatch(1);
        synchronized (sLock) {
            if (sLatch != null) {
                Log.e(TAG, "FilePicker: ja existe um seletor aberto");
                return null;
            }
            sLatch = latch;
            sResultCode = Activity.RESULT_CANCELED;
            sResultData = null;
        }

        activity.runOnUiThread(new Runnable() {
            @Override
            public void run() {
                try {
                    activity.startActivityForResult(request, REQUEST_CODE);
                } catch (Throwable t) {
                    Log.e(TAG, "FilePicker: falha ao abrir seletor", t);
                    deliver(Activity.RESULT_CANCELED, null);
                }
            }
        });

        boolean interrupted = false;
        while (true) {
            try {
                latch.await();
                break;
            } catch (InterruptedException e) {
                interrupted = true;
            }
        }
        if (interrupted) Thread.currentThread().interrupt();

        int code;
        Intent data;
        synchronized (sLock) {
            code = sResultCode;
            data = sResultData;
            sLatch = null;
        }
        if (code == Activity.RESULT_OK) return data != null ? data : new Intent();
        if (acceptAnyResult) return data != null ? data : new Intent();
        return null;
    }

    /** Chamado por GmIdeActivity.onActivityResult (thread de UI). */
    static boolean onActivityResult(int requestCode, int resultCode, Intent data) {
        if (requestCode != REQUEST_CODE) return false;
        deliver(resultCode, data);
        return true;
    }

    private static void deliver(int resultCode, Intent data) {
        CountDownLatch latch;
        synchronized (sLock) {
            latch = sLatch;
            sResultCode = resultCode;
            sResultData = data;
        }
        if (latch != null) latch.countDown();
    }
}
