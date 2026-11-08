package io.gmide;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Context;
import android.content.DialogInterface;
import android.util.Log;
import android.widget.Toast;

import org.json.JSONObject;

import java.io.File;
import java.io.FileInputStream;
import java.io.FileOutputStream;
import java.nio.charset.StandardCharsets;

// Consentimento da telemetria e itens gravados pelo lado Java (runner GX no WebView).
// O C# (host/Telemetry/Telemetry.cs) le o mesmo arquivo files/telemetry/consent:
// ausente = ainda nao perguntado, "1" = enviar, "0" = nao enviar (e apagar a fila).
public final class TelemetryConsent {
    private static final String TAG = "GmIde.Telemetry";

    private TelemetryConsent() {}

    static File dir(Context c) {
        File d = new File(c.getFilesDir(), "telemetry");
        d.mkdirs();
        return d;
    }

    /** -1 sem resposta, 0 recusado, 1 aceito. */
    public static int state(Context c) {
        File f = new File(dir(c), "consent");
        if (!f.exists()) return -1;
        return "1".equals(read(f)) ? 1 : 0;
    }

    public static void set(Context c, boolean on) {
        File d = dir(c);
        write(new File(d, "consent"), on ? "1" : "0", false);
        if (!on) {
            File[] files = d.listFiles();
            if (files != null) for (File f : files) if (!f.getName().equals("consent")) f.delete();
        }
        Log.i(TAG, "consentimento: " + (on ? "aceito" : "recusado"));
    }

    /** Primeira abertura: pergunta uma vez. Fechar sem escolher pergunta de novo na proxima abertura. */
    public static void askIfNeeded(final Activity a) {
        if (a.isFinishing() || state(a) != -1) return;
        new AlertDialog.Builder(a, android.R.style.Theme_DeviceDefault_Dialog_Alert)
            .setTitle("Ajudar a melhorar o GM IDE?")
            .setMessage("Enviar dados anônimos de uso e de falhas: versão do app e do runtime, modelo do aparelho, "
                + "resultado e duração dos builds e o texto dos erros e crashes, com os caminhos de arquivo removidos.\n\n"
                + "Não enviamos seus projetos, código GML nem dados de conta. Dá para mudar depois no menu ⋯ "
                + "da barra de atalhos.")
            .setPositiveButton("Permitir", new DialogInterface.OnClickListener() {
                public void onClick(DialogInterface d, int w) { set(a, true); }
            })
            .setNegativeButton("Não enviar", new DialogInterface.OnClickListener() {
                public void onClick(DialogInterface d, int w) { set(a, false); }
            })
            .show();
    }

    public static String menuLabel(Context c) {
        return "Dados de uso e falhas: " + (state(c) == 1 ? "enviando" : "desligado");
    }

    public static void toggle(Context c) {
        boolean on = state(c) != 1;
        set(c, on);
        Toast.makeText(c, on ? "Envio de dados de uso e falhas ligado" : "Envio desligado; a fila local foi apagada",
            Toast.LENGTH_SHORT).show();
    }

    /** Erro do runner GX (WebView, processo da IDE). Mesmo formato dos itens do C#. */
    public static void recordRunner(Context c, String kind, String type, String message, String stack) {
        try {
            if (state(c) == 0) return;
            File d = dir(c);
            JSONObject j = new JSONObject();
            j.put("type", "crash");
            String session = read(new File(d, "session"));
            if (session != null && session.indexOf(' ') > 0) j.put("session_id", session.substring(0, session.indexOf(' ')));
            j.put("ts", System.currentTimeMillis());
            j.put("process", "runner");
            j.put("kind", kind);
            j.put("exception_type", type);
            j.put("message", clip(message, 2000));
            j.put("stack", clip(stack, 8000));
            write(new File(d, "q-runner-" + android.os.Process.myPid() + ".jsonl"), j.toString() + "\n", true);
        } catch (Exception e) {
            Log.w(TAG, "recordRunner: " + e);
        }
    }

    /** Evento simples (sem sessao obrigatoria). */
    public static void recordEvent(Context c, String name, JSONObject props) {
        try {
            if (state(c) == 0) return;
            File d = dir(c);
            JSONObject j = new JSONObject();
            j.put("type", "event");
            String session = read(new File(d, "session"));
            if (session != null && session.indexOf(' ') > 0) j.put("session_id", session.substring(0, session.indexOf(' ')));
            j.put("ts", System.currentTimeMillis());
            j.put("name", name);
            j.put("props", props != null ? props : new JSONObject());
            write(new File(d, "q-runner-" + android.os.Process.myPid() + ".jsonl"), j.toString() + "\n", true);
        } catch (Exception e) {
            Log.w(TAG, "recordEvent: " + e);
        }
    }

    private static String clip(String s, int max) {
        return s == null ? null : s.length() <= max ? s : s.substring(0, max);
    }

    private static String read(File f) {
        if (!f.exists()) return null;
        try (FileInputStream in = new FileInputStream(f)) {
            byte[] b = new byte[(int) Math.min(f.length(), 4096)];
            int n = in.read(b);
            return n <= 0 ? "" : new String(b, 0, n, StandardCharsets.UTF_8).trim();
        } catch (Exception e) {
            return null;
        }
    }

    private static synchronized void write(File f, String s, boolean append) {
        try (FileOutputStream out = new FileOutputStream(f, append)) {
            out.write(s.getBytes(StandardCharsets.UTF_8));
        } catch (Exception e) {
            Log.w(TAG, "write " + f.getName() + ": " + e);
        }
    }
}
