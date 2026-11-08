package io.gmide;

import android.app.Activity;
import android.content.Intent;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.util.Log;
import android.view.View;
import android.view.WindowManager;
import android.webkit.ConsoleMessage;
import android.webkit.WebChromeClient;
import android.webkit.WebResourceRequest;
import android.webkit.WebResourceResponse;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;

import java.io.ByteArrayInputStream;
import java.io.File;
import java.io.FileInputStream;
import java.io.IOException;
import java.util.HashMap;
import java.util.Map;

/**
 * Roda o jogo do alvo GX (runner.html/js/wasm) gerado pelo "OperaGX Run" do Igor.
 * Aberto pelo BuildService (processo :build) quando o Igor imprime
 * "Attempting to open browser ... with URL "http://localhost:PORTA/runner.html"".
 *
 * Extras: "url" (a URL do Igor) e "root" (o -root do GMWebServer = pasta do runner).
 * Com "root", os arquivos sao servidos direto do disco numa origem https falsa
 * (https://appassets.androidplatform.net/, o mesmo truque do WebViewAssetLoader), via
 * shouldInterceptRequest: contexto seguro, MIME application/wasm correto e sem trafego
 * http em claro (que o WebView bloqueia sem usesCleartextTraffic). Sem "root", carrega a URL.
 *
 * Fechar (Voltar) encerra o Run: manda io.gmide.action.STOP_RUN ao BuildService, que
 * termina o Igor (esperando o webserver) com sucesso e reinicia o processo :build.
 * Sem lambdas/method refs (build-java.sh compila com -source 8 para o d8 do SDK .NET).
 */
public class GameRunnerActivity extends Activity {
    static final String TAG = "GmIde.Runner";
    static final String HOST = "appassets.androidplatform.net";
    static final String ACTION_STOP_RUN = "io.gmide.action.STOP_RUN";

    WebView web;
    File root;
    String rootCanon;
    String contentHost = HOST;
    boolean standalone;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        web = new WebView(this);
        WebSettings s = web.getSettings();
        s.setJavaScriptEnabled(true);
        s.setDomStorageEnabled(true);
        s.setDatabaseEnabled(true);
        s.setMediaPlaybackRequiresUserGesture(false);
        s.setAllowFileAccess(false);
        s.setAllowContentAccess(false);
        s.setLoadWithOverviewMode(true);
        s.setUseWideViewPort(true);
        web.setWebViewClient(new RunnerClient());
        web.setWebChromeClient(new WebChromeClient() {
            @Override
            public boolean onConsoleMessage(ConsoleMessage m) {
                Log.i(TAG, m.message() + " (" + m.sourceId() + ":" + m.lineNumber() + ")");
                if (m.messageLevel() == ConsoleMessage.MessageLevel.ERROR) reportConsoleError(m);
                return true;
            }
        });
        setContentView(web);
        hideSystemUi();
        load(getIntent());
    }

    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        if (intent.getStringExtra("url") == null && web != null) return;
        setIntent(intent);
        load(intent);
    }

    // Telemetria (docs/telemetria.md): "Uncaught ..." e excecao nao tratada do jogo no runner GX
    // e vira crash; outros console.error so contam. No maximo 5 registros por abertura.
    int consoleReports;

    void reportConsoleError(ConsoleMessage m) {
        if (consoleReports >= 5) return;
        consoleReports++;
        String msg = m.message() == null ? "" : m.message();
        String src = m.sourceId() == null ? "" : m.sourceId().replaceAll("^https?://[^/]+", "");
        if (msg.startsWith("Uncaught")) {
            int colon = msg.indexOf(':');
            String type = colon > 0 && colon < 60 ? msg.substring(0, colon) : "Uncaught";
            TelemetryConsent.recordRunner(this, "runner_js", type, msg, src + ":" + m.lineNumber());
        } else {
            try {
                org.json.JSONObject p = new org.json.JSONObject();
                p.put("source", src);
                TelemetryConsent.recordEvent(this, "runner.console_error", p);
            } catch (org.json.JSONException ignored) { }
        }
    }

    void load(Intent intent) {
        standalone = intent.getBooleanExtra("standalone", false);
        if (standalone) setTitle(intent.getStringExtra("title") == null ? "Prévia do jogo" : intent.getStringExtra("title"));
        contentHost = HOST;
        if (standalone) {
            try {
                byte[] hash = java.security.MessageDigest.getInstance("SHA-256").digest(intent.getStringExtra("storage_id").getBytes("UTF-8"));
                StringBuilder host = new StringBuilder("game-");
                for (int n = 0; n < 12; n++) host.append(String.format(java.util.Locale.ROOT, "%02x", hash[n] & 255));
                contentHost = host.append(".gmide.local").toString();
            } catch (Exception e) { Log.e(TAG, "Identificador da previa invalido", e); finish(); return; }
        }
        String url = intent.getStringExtra("url");
        String r = intent.getStringExtra("root");
        root = null;
        rootCanon = null;
        if (r != null) {
            File f = new File(r);
            try {
                if (f.isDirectory()) {
                    root = f;
                    rootCanon = f.getCanonicalPath();
                }
            } catch (IOException e) {
                Log.w(TAG, "root " + r + ": " + e);
            }
        }
        if (url == null) {
            Log.e(TAG, "sem URL");
            finish();
            return;
        }
        if (root != null) {
            Uri u = Uri.parse(url);
            String path = u.getEncodedPath();
            if (path == null || path.length() == 0 || path.equals("/")) path = "/runner.html";
            String q = u.getEncodedQuery();
            String local = "https://" + contentHost + path + (q != null ? "?" + q : "");
            Log.i(TAG, "Runner de " + rootCanon + " em " + local + " (Igor: " + url + ")");
            web.loadUrl(local);
        } else {
            Log.i(TAG, "Runner em " + url);
            web.loadUrl(url);
        }
    }

    class RunnerClient extends WebViewClient {
        // Sem este override o renderer que cai derruba o processo da IDE inteiro (mesmo processo).
        @Override
        public boolean onRenderProcessGone(WebView view, android.webkit.RenderProcessGoneDetail detail) {
            boolean crashed = Build.VERSION.SDK_INT >= 26 && detail.didCrash();
            Log.e(TAG, "Renderer do WebView encerrou (" + (crashed ? "crash" : "sistema liberou memoria") + ")");
            TelemetryConsent.recordRunner(GameRunnerActivity.this, crashed ? "native" : "oom",
                "RenderProcessGone", crashed ? "renderer crash" : "renderer killed by system", null);
            if (web == view) web = null;
            view.destroy();
            finish();
            return true;
        }

        @Override
        public WebResourceResponse shouldInterceptRequest(WebView view, WebResourceRequest req) {
            Uri u = req.getUrl();
            if (root == null || u == null || !contentHost.equals(u.getHost())) return null;
            return serve(u.getPath());
        }

        @Override
        public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest req) {
            Uri u = req.getUrl();
            if (u != null && contentHost.equals(u.getHost())) return false;
            String scheme = u == null ? null : u.getScheme();
            if ("http".equals(scheme) || "https".equals(scheme)) {
                // Links externos do jogo (url_open): navegador do aparelho.
                try {
                    Intent i = new Intent(Intent.ACTION_VIEW, u);
                    i.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK);
                    startActivity(i);
                } catch (Exception e) {
                    Log.w(TAG, "abrir " + u + ": " + e);
                }
                return true;
            }
            return false;
        }
    }

    WebResourceResponse serve(String path) {
        Map<String, String> headers = new HashMap<String, String>();
        headers.put("Cache-Control", "no-store");
        headers.put("Access-Control-Allow-Origin", "*");
        try {
            if (path == null || path.length() == 0 || path.equals("/")) path = "/runner.html";
            File f = new File(root, path.substring(1));
            String canon = f.getCanonicalPath();
            if (!canon.startsWith(rootCanon + File.separator) || !f.isFile()) {
                return new WebResourceResponse("text/plain", "utf-8", 404, "Not Found", headers,
                        new ByteArrayInputStream(new byte[0]));
            }
            String mime = mimeOf(f.getName());
            String enc = mime.startsWith("text/") || mime.endsWith("javascript") || mime.endsWith("json") ? "utf-8" : null;
            return new WebResourceResponse(mime, enc, 200, "OK", headers, new FileInputStream(f));
        } catch (IOException e) {
            Log.w(TAG, "servir " + path + ": " + e);
            return new WebResourceResponse("text/plain", "utf-8", 500, "Error", headers,
                    new ByteArrayInputStream(new byte[0]));
        }
    }

    static String mimeOf(String name) {
        String n = name.toLowerCase();
        int dot = n.lastIndexOf('.');
        String ext = dot < 0 ? "" : n.substring(dot + 1);
        if (ext.equals("html") || ext.equals("htm")) return "text/html";
        if (ext.equals("js") || ext.equals("mjs")) return "application/javascript";
        if (ext.equals("wasm")) return "application/wasm";
        if (ext.equals("json")) return "application/json";
        if (ext.equals("css")) return "text/css";
        if (ext.equals("png")) return "image/png";
        if (ext.equals("jpg") || ext.equals("jpeg")) return "image/jpeg";
        if (ext.equals("gif")) return "image/gif";
        if (ext.equals("svg")) return "image/svg+xml";
        if (ext.equals("ico")) return "image/x-icon";
        if (ext.equals("ogg")) return "audio/ogg";
        if (ext.equals("mp3")) return "audio/mpeg";
        if (ext.equals("wav")) return "audio/wav";
        if (ext.equals("mp4")) return "video/mp4";
        if (ext.equals("webm")) return "video/webm";
        if (ext.equals("txt")) return "text/plain";
        if (ext.equals("ttf")) return "font/ttf";
        if (ext.equals("woff")) return "font/woff";
        if (ext.equals("woff2")) return "font/woff2";
        return "application/octet-stream"; // game.unx, *.yydebug ...
    }

    void hideSystemUi() {
        if (Build.VERSION.SDK_INT >= 19) {
            getWindow().getDecorView().setSystemUiVisibility(
                    View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY | View.SYSTEM_UI_FLAG_FULLSCREEN
                            | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION | View.SYSTEM_UI_FLAG_LAYOUT_STABLE
                            | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN);
        }
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (hasFocus) hideSystemUi();
    }

    @Override
    protected void onPause() {
        super.onPause();
        if (web != null) web.onPause();
    }

    @Override
    protected void onResume() {
        super.onResume();
        if (web != null) web.onResume();
    }

    @Override
    protected void onDestroy() {
        if (isFinishing() && !standalone) stopRun();
        if (web != null) {
            web.loadUrl("about:blank");
            web.destroy();
            web = null;
        }
        super.onDestroy();
    }

    void stopRun() {
        try {
            Intent i = new Intent(ACTION_STOP_RUN);
            i.setClassName(getPackageName(), "io.gmide.BuildService");
            startService(i);
        } catch (Exception e) {
            Log.w(TAG, "STOP_RUN: " + e);
        }
    }

    @Override
    public void onBackPressed() {
        if (standalone) {
            Intent ide = new Intent();
            ide.setClassName(getPackageName(), "io.gmide.GmIdeActivity");
            ide.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_REORDER_TO_FRONT);
            startActivity(ide);
            finish();
        } else super.onBackPressed();
    }
}
