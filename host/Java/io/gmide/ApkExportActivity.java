package io.gmide;

import android.app.Activity;
import android.content.Context;
import android.content.Intent;
import android.content.res.ColorStateList;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Typeface;
import android.graphics.drawable.Drawable;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.StateListDrawable;
import android.net.Uri;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.text.Editable;
import android.text.InputType;
import android.text.TextUtils;
import android.text.TextWatcher;
import android.util.DisplayMetrics;
import android.util.Log;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.KeyEvent;
import android.view.View;
import android.view.ViewGroup;
import android.view.Window;
import android.view.WindowManager;
import android.view.inputmethod.EditorInfo;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ProgressBar;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import org.json.JSONException;
import org.json.JSONObject;

import java.io.File;
import java.io.FileOutputStream;
import java.io.InputStream;
import java.lang.ref.WeakReference;
import java.util.ArrayDeque;
import java.util.Locale;
import java.util.concurrent.LinkedBlockingQueue;
import java.util.concurrent.TimeUnit;
import java.util.regex.Pattern;

/**
 * Popup "Gerar APK" (docs/apk-export.md), no visual da IDE (skin Dark, Open Sans da propria IDE).
 *
 * Aberto pelo C# (ApkExport.cs) com o extra "json": session, project, display_name, package,
 * version, output_dir, file_name, icon_store, icon_path, keystore, cache_ok, cache_error.
 * Com o popup por cima a IDE (SDL) fica pausada, entao a conversa nao passa pela barra de atalhos:
 * uma thread do C# fica em {@link #nextRequest} esperando "build:{json}" ou "close", e devolve
 * progresso/resultado por {@link #onStatus}. Estados da tela: formulario -> gerando -> pronto/erro.
 * Interface montada em codigo (o host nao tem res/). Sem lambdas (build-java.sh usa -source 8).
 */
public class ApkExportActivity extends Activity {
    static final String TAG = "GmIde.Apk";
    static final int PICK_ICON = 41;
    static final Pattern PACKAGE = Pattern.compile("^[a-zA-Z][a-zA-Z0-9_]*(\\.[a-zA-Z][a-zA-Z0-9_]*){2,}$");
    static final Pattern VERSION = Pattern.compile("^\\d{1,4}(\\.\\d{1,4}){0,3}$");
    static final String[] JAVA_KEYWORDS = {
        "abstract", "assert", "boolean", "break", "byte", "case", "catch", "char", "class", "const", "continue",
        "default", "do", "double", "else", "enum", "extends", "final", "finally", "float", "for", "goto", "if",
        "implements", "import", "instanceof", "int", "interface", "long", "native", "new", "package", "private",
        "protected", "public", "return", "short", "static", "strictfp", "super", "switch", "synchronized", "this",
        "throw", "throws", "transient", "try", "void", "volatile", "while", "false", "null", "true",
    };

    // Cores tiradas da IDE (skin Dark): barra de menus, cabecalho de painel, campos, aba ativa.
    static final int C_SCRIM = 0xB0000000;
    static final int C_CARD = 0xFF282828;
    static final int C_HEADER = 0xFF3F3F3F;
    static final int C_FOOTER = 0xFF232323;
    static final int C_FIELD = 0xFF181818;
    static final int C_BORDER = 0xFF4A4A4A;
    static final int C_DIVIDER = 0xFF1A1A1A;
    static final int C_ACCENT = 0xFF039D5B;
    static final int C_ACCENT_DOWN = 0xFF027A47;
    static final int C_BUTTON = 0xFF3F3F3F;
    static final int C_BUTTON_DOWN = 0xFF535353;
    static final int C_TEXT = 0xFFFFFFFF;
    static final int C_DIM = 0xFF9B9B9B;
    static final int C_ERROR = 0xFFE5534B;
    static final int C_WARN_BG = 0xFF3B3524;
    static final int C_WARN = 0xFFEADBB2;

    // ------------------------------------------------------------------ canal com o C#

    static final LinkedBlockingQueue<String> sRequests = new LinkedBlockingQueue<String>();
    static volatile int sSession;
    /** Ultima sessao cujo popup fechou. */
    static volatile int sClosed;
    static WeakReference<ApkExportActivity> sInstance = new WeakReference<ApkExportActivity>(null);
    static Context sApp;
    static final Handler sMain = new Handler(Looper.getMainLooper());

    /** Chamado pelo C# (thread da sessao). null = nada ainda; "close" = popup fechou ou foi trocado. */
    public static String nextRequest(int session, long timeoutMs) {
        if (session <= sClosed || session < sSession) return "close";
        if (session > sSession) {
            // O C# abre a sessao antes de a activity existir: espera ela ser criada.
            try { Thread.sleep(Math.min(timeoutMs, 200)); } catch (InterruptedException e) { return "close"; }
            return null;
        }
        try {
            String r = sRequests.poll(timeoutMs, TimeUnit.MILLISECONDS);
            if (session != sSession) {
                if (r != null && !r.equals("close")) sRequests.offer(r); // era do popup novo
                return "close";
            }
            return r;
        } catch (InterruptedException e) {
            return "close";
        }
    }

    /** Chamado pelo C# de qualquer thread: {"state":"progress|done|error","message":...}. */
    public static void onStatus(final String json) {
        sMain.post(new Runnable() {
            public void run() {
                JSONObject j;
                try { j = new JSONObject(json); } catch (JSONException e) { return; }
                ApkExportActivity a = sInstance.get();
                if (a != null && !a.isFinishing()) { a.applyStatus(j); return; }
                // Popup fechado no meio: avisa o fim com um Toast.
                String st = j.optString("state");
                if (sApp == null) return;
                if (st.equals("done"))
                    Toast.makeText(sApp, "Jogo exportado: " + new File(j.optString("message")).getName(), Toast.LENGTH_LONG).show();
                else if (st.equals("error"))
                    Toast.makeText(sApp, "Falha ao exportar: " + j.optString("message"), Toast.LENGTH_LONG).show();
            }
        });
    }

    // ------------------------------------------------------------------ estado

    static final int FORM = 0, BUILDING = 1, DONE = 2, ERROR = 3;

    JSONObject initial;
    int session;
    int state = FORM;
    float density;
    Typeface regular = Typeface.DEFAULT, semibold = Typeface.DEFAULT_BOLD, mono = Typeface.MONOSPACE;

    EditText name, pkg, version, fileName;
    TextView nameErr, pkgErr, versionErr, fileErr, outputHint;
    ImageView iconPreview;
    TextView iconPlaceholder, iconRemove;
    String iconPath;
    boolean iconOverride;

    FrameLayout body;
    LinearLayout footer;
    TextView footerNote;
    TextView primaryBtn, secondaryBtn;
    View formView;

    TextView progressStep, progressLog;
    final ArrayDeque<String> logLines = new ArrayDeque<String>();
    String resultPath;
    String target = "android", previewPath;
    EditText windowWidth, windowHeight;

    JSONObject targetOptions() {
        JSONObject all = initial.optJSONObject("targets");
        JSONObject options = all == null ? null : all.optJSONObject(target);
        return options == null ? initial : options;
    }

    void selectTarget(String next) {
        if (state != FORM || next.equals(target)) return;
        try {
            JSONObject old = targetOptions();
            old.put("display_name", name.getText().toString());
            old.put("version", version.getText().toString());
            old.put("package", pkg.getText().toString());
            old.put("file_name", fileName.getText().toString());
            old.put("icon_path", iconPath == null ? "" : iconPath);
            old.put("icon_override", iconOverride);
            if (windowWidth != null) {
                old.put("window_width", windowWidth.getText().toString());
                old.put("window_height", windowHeight.getText().toString());
            }
        } catch (JSONException e) { toast(e.getMessage()); }
        target = next;
        iconOverride = targetOptions().optBoolean("icon_override", false);
        formView = buildForm(); showForm();
    }

    @Override
    protected void onCreate(Bundle b) {
        super.onCreate(b);
        sApp = getApplicationContext();
        density = getResources().getDisplayMetrics().density;
        Window w = getWindow();
        w.setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_PAN | WindowManager.LayoutParams.SOFT_INPUT_STATE_HIDDEN);
        w.addFlags(WindowManager.LayoutParams.FLAG_FULLSCREEN);
        hideSystemBars();
        try {
            String j = getIntent().getStringExtra("json");
            initial = j != null ? new JSONObject(j) : new JSONObject();
        } catch (JSONException e) {
            initial = new JSONObject();
        }
        session = initial.optInt("session", 0);
        sSession = session;
        sRequests.clear();
        sInstance = new WeakReference<ApkExportActivity>(this);
        loadFonts();
        setContentView(build());
        showForm();
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (hasFocus) hideSystemBars();
    }

    @Override
    protected void onDestroy() {
        super.onDestroy();
        if (sInstance.get() == this) sInstance = new WeakReference<ApkExportActivity>(null);
        if (isFinishing()) {
            if (session > sClosed) sClosed = session;
            if (sSession == session) sRequests.offer("close");
        }
    }

    @Override
    public void onBackPressed() {
        if (state == ERROR) { showForm(); return; }
        if (state == BUILDING) toast("O APK continua sendo gerado; aviso quando terminar.");
        finish();
    }

    void hideSystemBars() {
        getWindow().getDecorView().setSystemUiVisibility(View.SYSTEM_UI_FLAG_LAYOUT_STABLE
                | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
                | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION | View.SYSTEM_UI_FLAG_FULLSCREEN
                | View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY);
    }

    void loadFonts() {
        File dir = new File(getFilesDir(), "ide/Fonts");
        regular = font(new File(dir, "OpenSans-Regular.ttf"), Typeface.DEFAULT);
        semibold = font(new File(dir, "OpenSans-Semibold.ttf"), Typeface.DEFAULT_BOLD);
        mono = font(new File(dir, "DroidSansMono.ttf"), Typeface.MONOSPACE);
    }

    static Typeface font(File f, Typeface fallback) {
        try { return f.isFile() ? Typeface.createFromFile(f) : fallback; }
        catch (Throwable t) { return fallback; }
    }

    int dp(float v) { return Math.round(v * density); }

    // ------------------------------------------------------------------ montagem

    View build() {
        DisplayMetrics m = getResources().getDisplayMetrics();
        int sw = Math.max(m.widthPixels, m.heightPixels), sh = Math.min(m.widthPixels, m.heightPixels);

        FrameLayout root = new FrameLayout(this);
        root.setBackgroundColor(C_SCRIM);
        root.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { if (state != BUILDING) finish(); }
        });

        LinearLayout card = new LinearLayout(this);
        card.setOrientation(LinearLayout.VERTICAL);
        card.setClickable(true); // toques no cartao nao fecham o popup
        card.setBackground(rounded(C_CARD, dp(4), 0xFF1C1C1C));
        card.setClipToOutline(true);
        card.setElevation(dp(16));
        int cardW = Math.min(sw - dp(32), dp(720));
        root.addView(card, new FrameLayout.LayoutParams(cardW, ViewGroup.LayoutParams.WRAP_CONTENT, Gravity.CENTER));

        // Cabecalho (como o titulo dos paineis da IDE)
        LinearLayout header = new LinearLayout(this);
        header.setOrientation(LinearLayout.HORIZONTAL);
        header.setGravity(Gravity.CENTER_VERTICAL);
        header.setBackgroundColor(C_HEADER);
        header.setPadding(dp(16), 0, dp(4), 0);
        TextView title = text("Exportar jogo", 15, C_TEXT, semibold);
        header.addView(title, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        String project = initial.optString("project", "");
        if (project.length() > 0) {
            TextView p = text(project, 13, C_DIM, regular);
            p.setSingleLine(true);
            p.setEllipsize(TextUtils.TruncateAt.END);
            p.setMaxWidth(cardW / 3);
            header.addView(p);
        }
        TextView close = text("✕", 16, C_DIM, regular);
        close.setGravity(Gravity.CENTER);
        close.setBackground(pressable(0x00000000, 0x22FFFFFF, 0x00000000, dp(3)));
        close.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { onBackPressed(); }
        });
        header.addView(close, new LinearLayout.LayoutParams(dp(40), dp(40)));
        card.addView(header, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(44)));

        // Corpo rolavel com altura maxima (o cartao nunca passa da tela)
        final int maxBody = sh - dp(24) - dp(44) - dp(60);
        ScrollView sv = new ScrollView(this) {
            @Override
            protected void onMeasure(int wSpec, int hSpec) {
                super.onMeasure(wSpec, MeasureSpec.makeMeasureSpec(maxBody, MeasureSpec.AT_MOST));
            }
        };
        sv.setFillViewport(true);
        sv.setVerticalScrollBarEnabled(true);
        body = new FrameLayout(this);
        sv.addView(body, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        card.addView(sv, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        // Rodape: observacao a esquerda, botoes a direita
        View div = new View(this);
        div.setBackgroundColor(C_DIVIDER);
        card.addView(div, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(1)));
        footer = new LinearLayout(this);
        footer.setOrientation(LinearLayout.HORIZONTAL);
        footer.setGravity(Gravity.CENTER_VERTICAL);
        footer.setBackgroundColor(C_FOOTER);
        footer.setPadding(dp(16), dp(10), dp(12), dp(10));
        footerNote = text("", 12, C_DIM, regular);
        footerNote.setSingleLine(true);
        footerNote.setEllipsize(TextUtils.TruncateAt.MIDDLE);
        footer.addView(footerNote, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        secondaryBtn = button("", false);
        primaryBtn = button("", true);
        LinearLayout.LayoutParams blp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(36));
        blp.leftMargin = dp(8);
        footer.addView(secondaryBtn, blp);
        footer.addView(primaryBtn, new LinearLayout.LayoutParams(blp));
        card.addView(footer, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        formView = buildForm();
        return root;
    }

    View buildForm() {
        LinearLayout col = new LinearLayout(this);
        col.setOrientation(LinearLayout.VERTICAL);
        col.setPadding(dp(16), dp(14), dp(16), dp(14));

        LinearLayout destinations = new LinearLayout(this);
        final String[] ids = { "android", "web", "windows" };
        String[] labels = { "Android · APK", "Web · ZIP", "Windows · EXE" };
        for (int i = 0; i < ids.length; i++) {
            final String id = ids[i];
            TextView tab = button(labels[i], target.equals(id));
            tab.setOnClickListener(new View.OnClickListener() { public void onClick(View v) { selectTarget(id); } });
            LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(0, dp(44), 1);
            lp.setMargins(0, 0, dp(6), dp(12)); destinations.addView(tab, lp);
        }
        col.addView(destinations);

        LinearLayout row = new LinearLayout(this);
        row.setOrientation(LinearLayout.HORIZONTAL);
        col.addView(row);

        // Coluna do icone
        LinearLayout iconCol = new LinearLayout(this);
        iconCol.setOrientation(LinearLayout.VERTICAL);
        iconCol.setGravity(Gravity.CENTER_HORIZONTAL);
        iconCol.addView(label("Ícone"), new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        FrameLayout iconBox = new FrameLayout(this);
        iconBox.setBackground(pressable(C_FIELD, 0xFF222222, C_FIELD, dp(4)));
        iconBox.setForeground(rounded(0x00000000, dp(4), C_BORDER));
        iconBox.setClipToOutline(true);
        iconBox.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { chooseIcon(); }
        });
        iconPreview = new ImageView(this);
        iconPreview.setScaleType(ImageView.ScaleType.CENTER_CROP);
        iconBox.addView(iconPreview, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        iconPlaceholder = text("+\nEscolher", 12, C_DIM, regular);
        iconPlaceholder.setGravity(Gravity.CENTER);
        iconBox.addView(iconPlaceholder, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        iconCol.addView(iconBox, new LinearLayout.LayoutParams(dp(96), dp(96)));
        iconRemove = text("Usar o do jogo", 12, C_ACCENT, regular);
        iconRemove.setPadding(dp(4), dp(8), dp(4), dp(4));
        iconRemove.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { clearIcon(); }
        });
        iconCol.addView(iconRemove);
        row.addView(iconCol, new LinearLayout.LayoutParams(dp(112), ViewGroup.LayoutParams.WRAP_CONTENT));

        // Campos
        LinearLayout fields = new LinearLayout(this);
        fields.setOrientation(LinearLayout.VERTICAL);
        LinearLayout.LayoutParams fl = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1);
        fl.leftMargin = dp(16);
        row.addView(fields, fl);

        LinearLayout r1 = new LinearLayout(this);
        r1.setOrientation(LinearLayout.HORIZONTAL);
        fields.addView(r1);
        LinearLayout nameCol = column();
        name = field(nameCol, "Nome do jogo", targetOptions().optString("display_name", ""), InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_CAP_WORDS);
        nameErr = errorText(nameCol);
        r1.addView(nameCol, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
        LinearLayout verCol = column();
        version = field(verCol, "Versão", targetOptions().optString("version", "1.0.0.0"), InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        versionErr = errorText(verCol);
        LinearLayout.LayoutParams vl = new LinearLayout.LayoutParams(dp(130), ViewGroup.LayoutParams.WRAP_CONTENT);
        vl.leftMargin = dp(12);
        r1.addView(verCol, vl);

        pkg = field(fields, target.equals("android") ? "Pacote (identificador único do app)" : "Identificador dos dados salvos", targetOptions().optString("package", initial.optString("package", "com.company.game")),
                InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS | InputType.TYPE_TEXT_VARIATION_URI);
        pkgErr = errorText(fields);

        windowWidth = null; windowHeight = null;
        if (target.equals("windows")) {
            LinearLayout dimensions = new LinearLayout(this);
            LinearLayout wc = column(), hc = column();
            windowWidth = field(wc, "Largura da janela", targetOptions().optString("window_width", "1280"), InputType.TYPE_CLASS_NUMBER);
            windowHeight = field(hc, "Altura da janela", targetOptions().optString("window_height", "720"), InputType.TYPE_CLASS_NUMBER);
            dimensions.addView(wc, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
            dimensions.addView(hc, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1));
            fields.addView(dimensions);
        }
        String fn = targetOptions().optString("file_name", "jogo" + extension());
        if (fn.toLowerCase(Locale.ROOT).endsWith(extension())) fn = fn.substring(0, fn.length() - 4);
        fileName = field(fields, "Salvar como", fn, InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_FLAG_NO_SUGGESTIONS);
        fileName.setImeOptions(EditorInfo.IME_ACTION_DONE | EditorInfo.IME_FLAG_NO_EXTRACT_UI | EditorInfo.IME_FLAG_NO_FULLSCREEN);
        fileName.setOnEditorActionListener(new TextView.OnEditorActionListener() {
            public boolean onEditorAction(TextView v, int actionId, KeyEvent e) {
                if (actionId == EditorInfo.IME_ACTION_DONE) { submit(); return true; }
                return false;
            }
        });
        fileErr = errorText(fields);
        outputHint = text("", 11, C_DIM, regular);
        outputHint.setPadding(0, dp(2), 0, 0);
        fields.addView(outputHint);

        TextWatcher w = new TextWatcher() {
            public void beforeTextChanged(CharSequence s, int a, int b, int c) { }
            public void onTextChanged(CharSequence s, int a, int b, int c) { }
            public void afterTextChanged(Editable s) { validate(); }
        };
        name.addTextChangedListener(w);
        version.addTextChangedListener(w);
        pkg.addTextChangedListener(w);
        fileName.addTextChangedListener(w);
        if (windowWidth != null) { windowWidth.addTextChangedListener(w); windowHeight.addTextChangedListener(w); }

        String ip = targetOptions().optString("icon_path", "");
        if (ip.length() > 0 && !ip.equals("null") && new File(ip).isFile()) showIcon(ip);
        else showIcon(null);
        return col;
    }

    // ------------------------------------------------------------------ estados

    void setBody(View v) {
        body.removeAllViews();
        if (v.getParent() != null) ((ViewGroup) v.getParent()).removeView(v);
        body.addView(v, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
    }

    void showForm() {
        state = FORM;
        setBody(formView);
        String ks = new File(initial.optString("keystore", "gmide-release.p12")).getName();
        footerNote.setText(target.equals("android") ? "Assinatura: " + ks : target.equals("web") ? "WebAssembly · pronto para hospedagem web" : "EXE único · Windows x64 · requer WebView2");
        secondaryBtn.setText("Cancelar");
        secondaryBtn.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { finish(); }
        });
        primaryBtn.setText(target.equals("android") ? "Gerar APK" : target.equals("windows") ? "Gerar EXE" : "Gerar ZIP");
        primaryBtn.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { submit(); }
        });
        validate();
    }

    void showBuilding() {
        state = BUILDING;
        hideKeyboard();
        LinearLayout col = new LinearLayout(this);
        col.setOrientation(LinearLayout.VERTICAL);
        col.setPadding(dp(20), dp(20), dp(20), dp(20));
        col.addView(text("Gerando " + outputFile().getName() + "...", 15, C_TEXT, semibold));
        ProgressBar bar = new ProgressBar(this, null, android.R.attr.progressBarStyleHorizontal);
        bar.setIndeterminate(true);
        bar.setIndeterminateTintList(ColorStateList.valueOf(C_ACCENT));
        LinearLayout.LayoutParams bl = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(8));
        bl.topMargin = dp(12);
        bl.bottomMargin = dp(8);
        col.addView(bar, bl);
        progressStep = text("Começando...", 13, C_DIM, regular);
        col.addView(progressStep);
        progressLog = text("", 11, 0xFF7A7A7A, mono);
        progressLog.setPadding(dp(10), dp(8), dp(10), dp(8));
        progressLog.setBackground(rounded(C_FIELD, dp(3), 0));
        progressLog.setMinLines(4);
        LinearLayout.LayoutParams ll = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        ll.topMargin = dp(12);
        col.addView(progressLog, ll);
        logLines.clear();
        setBody(col);
        footerNote.setText("Você pode fechar; a exportação continua em andamento.");
        secondaryBtn.setVisibility(View.GONE);
        primaryBtn.setText("Fechar");
        setEnabled(primaryBtn, true);
        primaryBtn.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { onBackPressed(); }
        });
        // Fechar durante a geracao e secundario, nao a acao principal
        primaryBtn.setBackground(buttonBg(false));
    }

    void showResult(boolean ok, String message, JSONObject j) {
        state = ok ? DONE : ERROR;
        LinearLayout col = new LinearLayout(this);
        col.setOrientation(LinearLayout.HORIZONTAL);
        col.setPadding(dp(20), dp(20), dp(20), dp(20));
        TextView badge = text(ok ? "✓" : "!", 22, C_TEXT, semibold);
        badge.setGravity(Gravity.CENTER);
        GradientDrawable circle = new GradientDrawable();
        circle.setShape(GradientDrawable.OVAL);
        circle.setColor(ok ? C_ACCENT : C_ERROR);
        badge.setBackground(circle);
        col.addView(badge, new LinearLayout.LayoutParams(dp(44), dp(44)));
        LinearLayout texts = new LinearLayout(this);
        texts.setOrientation(LinearLayout.VERTICAL);
        LinearLayout.LayoutParams tl = new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1);
        tl.leftMargin = dp(16);
        col.addView(texts, tl);
        if (ok) {
            resultPath = message;
            File f = new File(message);
            previewPath = j.optString("preview", "");
            texts.addView(text(target.equals("android") ? "APK gerado" : "Pacote gerado", 16, C_TEXT, semibold));
            String info = f.getName() + "  ·  " + size(j.optLong("size", f.length())) + "  ·  " + j.optDouble("seconds", 0) + " s";
            TextView t = text(info, 13, C_TEXT, regular);
            t.setPadding(0, dp(4), 0, 0);
            texts.addView(t);
            TextView p = text(f.getParent() + "/", 12, C_DIM, regular);
            p.setPadding(0, dp(2), 0, 0);
            texts.addView(p);
            TextView pk = text("Pacote " + j.optString("package", ""), 12, C_DIM, regular);
            texts.addView(pk);
            footerNote.setText(target.equals("android") ? "Para atualizar, use sempre a mesma assinatura." : target.equals("windows") ? "Envie somente este EXE e abra no Windows com WebView2." : "Hospede o ZIP extraído para jogar no navegador.");
            secondaryBtn.setVisibility(View.VISIBLE);
            secondaryBtn.setText(target.equals("android") ? "Fechar" : target.equals("windows") ? "Compartilhar EXE" : "Compartilhar ZIP");
            secondaryBtn.setOnClickListener(new View.OnClickListener() {
                public void onClick(View v) { if (target.equals("android")) finish(); else share(); }
            });
            primaryBtn.setText(target.equals("android") ? "Instalar" : "Prévia web");
            primaryBtn.setBackground(buttonBg(true));
            primaryBtn.setOnClickListener(new View.OnClickListener() {
                public void onClick(View v) { if (target.equals("android")) install(); else preview(); }
            });
        } else {
            texts.addView(text("Não foi possível exportar o jogo", 16, C_TEXT, semibold));
            TextView t = text(message, 13, C_DIM, regular);
            t.setPadding(0, dp(4), 0, 0);
            t.setTextIsSelectable(true);
            texts.addView(t);
            footerNote.setText("Detalhes no log (Download/gmide-log.txt).");
            secondaryBtn.setVisibility(View.VISIBLE);
            secondaryBtn.setText("Fechar");
            secondaryBtn.setOnClickListener(new View.OnClickListener() {
                public void onClick(View v) { finish(); }
            });
            primaryBtn.setText("Voltar");
            primaryBtn.setBackground(buttonBg(true));
            primaryBtn.setOnClickListener(new View.OnClickListener() {
                public void onClick(View v) { showForm(); }
            });
        }
        setEnabled(primaryBtn, true);
        setBody(col);
    }

    void applyStatus(JSONObject j) {
        String st = j.optString("state");
        String msg = j.optString("message");
        if (st.equals("progress")) {
            if (state != BUILDING) return;
            progressStep.setText(msg);
            logLines.addLast(msg);
            while (logLines.size() > 6) logLines.removeFirst();
            progressLog.setText(TextUtils.join("\n", logLines));
        } else if (st.equals("done")) {
            showResult(true, msg, j);
        } else if (st.equals("error")) {
            showResult(false, msg, j);
        }
    }

    // ------------------------------------------------------------------ validacao e envio

    boolean validate() {
        if (name == null) return false;
        boolean ok = true;
        String n = name.getText().toString().trim();
        ok &= setError(nameErr, n.length() == 0 ? "Informe o nome do jogo" : n.length() > 200 || n.indexOf('\n') >= 0 || n.indexOf('"') >= 0 ? "Use até 200 caracteres, sem aspas ou quebras de linha" : null);
        String v = version.getText().toString().trim();
        ok &= setError(versionErr, VERSION.matcher(v).matches() ? null : "Use números, ex.: 1.0.0.0");
        ok &= setError(pkgErr, packageError(pkg.getText().toString().trim()));
        String f = fileName.getText().toString().trim();
        ok &= setError(fileErr, f.length() == 0 ? "Informe o nome do arquivo"
                : f.indexOf('/') >= 0 || f.indexOf('\\') >= 0 || f.equals(".") || f.equals("..") ? "Só o nome do arquivo, sem pastas" : null);
        if (windowWidth != null) {
            try {
                int width = Integer.parseInt(windowWidth.getText().toString()), height = Integer.parseInt(windowHeight.getText().toString());
                if (width < 320 || width > 7680 || height < 240 || height > 4320) ok &= setError(fileErr, "Janela: largura 320–7680, altura 240–4320");
            } catch (NumberFormatException e) { ok &= setError(fileErr, "Informe a largura e a altura da janela"); }
        }
        outputHint.setText(f.length() > 0 ? "Em " + shortDir(initial.optString("output_dir", "")) + "/" + outputFile().getName() : "");
        setEnabled(primaryBtn, ok);
        return ok;
    }

    static String packageError(String p) {
        if (p.length() == 0) return "Informe o pacote, ex.: com.empresa.jogo";
        if (!PACKAGE.matcher(p).matches())
            return "Use 3 partes ou mais separadas por ponto; cada parte começa com letra (ex.: com.empresa.jogo)";
        for (String part : p.split("\\."))
            for (String k : JAVA_KEYWORDS)
                if (part.equals(k)) return "\"" + part + "\" é palavra reservada do Java";
        return null;
    }

    boolean setError(TextView t, String msg) {
        t.setText(msg == null ? "" : msg);
        t.setVisibility(msg == null ? View.GONE : View.VISIBLE);
        return msg == null;
    }

    File outputFile() {
        String f = fileName.getText().toString().trim();
        if (!f.toLowerCase(Locale.ROOT).endsWith(extension())) f += extension();
        String dir = initial.optString("output_dir", "/storage/emulated/0/Download");
        return new File(dir, f);
    }

    String extension() { return target.equals("android") ? ".apk" : target.equals("windows") ? ".exe" : ".zip"; }

    static String shortDir(String dir) {
        String root = "/storage/emulated/0/";
        return dir.startsWith(root) ? dir.substring(root.length()) : dir;
    }

    void submit() {
        if (state != FORM) return;
        if (!validate()) return;
        try {
            JSONObject j = new JSONObject();
            j.put("target", target);
            j.put("display_name", name.getText().toString().trim());
            j.put("package", pkg.getText().toString().trim());
            j.put("version", version.getText().toString().trim());
            j.put("output", outputFile().getAbsolutePath());
            if ((iconOverride || !target.equals("android")) && iconPath != null) j.put("icon_path", iconPath);
            if (windowWidth != null) {
                j.put("window_width", Integer.parseInt(windowWidth.getText().toString()));
                j.put("window_height", Integer.parseInt(windowHeight.getText().toString()));
            }
            Log.i(TAG, "Gerar APK: " + j);
            showBuilding();
            sRequests.offer("build:" + j.toString());
        } catch (JSONException e) {
            toast("Erro: " + e.getMessage());
        }
    }

    void install() {
        if (resultPath == null) return;
        String err = ApkInstaller.install(this, resultPath);
        if (err != null) toast("Não consegui abrir a instalação: " + err);
        else finish();
    }

    void preview() {
        if (previewPath == null || !new File(previewPath, "index.html").isFile()) { toast("Arquivos da prévia indisponíveis; gere o pacote novamente."); return; }
        Intent i = new Intent(this, GameRunnerActivity.class);
        i.putExtra("root", previewPath); i.putExtra("url", "https://appassets.androidplatform.net/index.html");
        i.putExtra("standalone", true); i.putExtra("storage_id", pkg.getText().toString().trim());
        i.putExtra("title", name.getText().toString().trim());
        startActivity(i);
        finish(); // Release the export session; Back from the game returns to the IDE.
    }

    void share() {
        try {
            Uri uri = (Uri) Class.forName("androidx.core.content.FileProvider")
                    .getMethod("getUriForFile", Context.class, String.class, File.class)
                    .invoke(null, this, getPackageName() + ".exports", new File(resultPath));
            Intent i = new Intent(Intent.ACTION_SEND); i.setType(target.equals("windows") ? "application/vnd.microsoft.portable-executable" : "application/zip");
            i.putExtra(Intent.EXTRA_STREAM, uri); i.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
            startActivity(Intent.createChooser(i, "Compartilhar jogo"));
        } catch (Exception e) { toast("Não foi possível compartilhar: " + e.getMessage()); }
    }

    // ------------------------------------------------------------------ icone

    void chooseIcon() {
        Intent i = new Intent(Intent.ACTION_OPEN_DOCUMENT);
        i.addCategory(Intent.CATEGORY_OPENABLE);
        i.setType("image/*");
        i.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
        startActivityForResult(i, PICK_ICON);
    }

    String iconStore() {
        String base = initial.optString("icon_store", "");
        return base.length() == 0 ? "" : base + "." + target;
    }

    void clearIcon() {
        String store = iconStore();
        if (store.length() > 0) new File(store).delete();
        iconOverride = false;
        String projectIcon = targetOptions().optString("icon_path", "");
        showIcon(projectIcon.length() > 0 && !projectIcon.equals("null") ? projectIcon : null);
    }

    void showIcon(String path) {
        Bitmap bm = null;
        if (path != null) {
            BitmapFactory.Options bounds = new BitmapFactory.Options();
            bounds.inJustDecodeBounds = true;
            BitmapFactory.decodeFile(path, bounds);
            BitmapFactory.Options o = new BitmapFactory.Options();
            o.inSampleSize = Math.max(1, Math.max(bounds.outWidth, bounds.outHeight) / 256);
            bm = BitmapFactory.decodeFile(path, o);
        }
        iconPath = bm != null ? path : null;
        iconPreview.setImageBitmap(bm);
        iconPlaceholder.setVisibility(bm != null ? View.GONE : View.VISIBLE);
        iconRemove.setVisibility(iconOverride ? View.VISIBLE : View.INVISIBLE);
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        hideSystemBars();
        if (requestCode != PICK_ICON || resultCode != RESULT_OK || data == null || data.getData() == null) return;
        Uri uri = data.getData();
        String store = iconStore();
        File dest = store.length() > 0 ? new File(store) : new File(getFilesDir(), "apk-export/icon-source");
        File tmp = new File(dest.getPath() + ".tmp");
        try {
            File parent = dest.getParentFile();
            if (parent != null) parent.mkdirs();
            InputStream in = getContentResolver().openInputStream(uri);
            if (in == null) throw new IllegalStateException("Não foi possível abrir a imagem.");
            FileOutputStream out = new FileOutputStream(tmp);
            try {
                byte[] buffer = new byte[32768];
                int n;
                long total = 0;
                while ((n = in.read(buffer)) != -1) {
                    total += n;
                    if (total > 24L * 1024 * 1024) throw new IllegalArgumentException("A imagem precisa ter menos de 24 MB.");
                    out.write(buffer, 0, n);
                }
            } finally {
                out.close();
                in.close();
            }
            BitmapFactory.Options bounds = new BitmapFactory.Options();
            bounds.inJustDecodeBounds = true;
            BitmapFactory.decodeFile(tmp.getAbsolutePath(), bounds);
            if (bounds.outWidth <= 0 || bounds.outHeight <= 0) throw new IllegalArgumentException("O arquivo escolhido não é uma imagem válida.");
            if (!tmp.renameTo(dest)) throw new IllegalStateException("Não consegui guardar a imagem.");
            iconOverride = true;
            showIcon(dest.getAbsolutePath());
        } catch (Exception e) {
            tmp.delete();
            toast("Não consegui usar essa imagem: " + e.getMessage());
        }
    }

    // ------------------------------------------------------------------ pecas visuais

    TextView text(String s, float sp, int color, Typeface tf) {
        TextView t = new TextView(this);
        t.setText(s);
        t.setTextSize(TypedValue.COMPLEX_UNIT_SP, sp);
        t.setTextColor(color);
        t.setTypeface(tf);
        t.setIncludeFontPadding(false);
        t.setLineSpacing(dp(2), 1f);
        return t;
    }

    TextView label(String s) {
        TextView t = text(s, 12, C_DIM, regular);
        t.setPadding(0, 0, 0, dp(5));
        return t;
    }

    LinearLayout column() {
        LinearLayout c = new LinearLayout(this);
        c.setOrientation(LinearLayout.VERTICAL);
        return c;
    }

    TextView errorText(LinearLayout parent) {
        TextView t = text("", 11, C_ERROR, regular);
        t.setPadding(0, dp(3), 0, 0);
        t.setVisibility(View.GONE);
        parent.addView(t);
        return t;
    }

    EditText field(LinearLayout parent, String caption, String value, int type) {
        TextView l = label(caption);
        LinearLayout.LayoutParams ll = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        ll.topMargin = parent.getChildCount() > 0 ? dp(10) : 0;
        parent.addView(l, ll);
        EditText e = new EditText(this);
        e.setText(value);
        e.setSingleLine(true);
        e.setInputType(type);
        e.setTypeface(regular);
        e.setTextSize(TypedValue.COMPLEX_UNIT_SP, 14);
        e.setTextColor(C_TEXT);
        e.setHighlightColor(0xFF2F6B50);
        e.setPadding(dp(10), dp(8), dp(10), dp(8));
        e.setImeOptions(EditorInfo.IME_ACTION_NEXT | EditorInfo.IME_FLAG_NO_EXTRACT_UI | EditorInfo.IME_FLAG_NO_FULLSCREEN);
        StateListDrawable bg = new StateListDrawable();
        bg.addState(new int[]{android.R.attr.state_focused}, rounded(C_FIELD, dp(3), C_ACCENT));
        bg.addState(new int[]{}, rounded(C_FIELD, dp(3), C_BORDER));
        e.setBackground(bg);
        if (android.os.Build.VERSION.SDK_INT >= 29) {
            GradientDrawable cursor = new GradientDrawable();
            cursor.setColor(C_ACCENT);
            cursor.setSize(dp(2), dp(18));
            e.setTextCursorDrawable(cursor);
        }
        parent.addView(e, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(38)));
        return e;
    }

    TextView button(String s, boolean primary) {
        TextView b = text(s, 14, C_TEXT, semibold);
        b.setGravity(Gravity.CENTER);
        b.setPadding(dp(18), 0, dp(18), 0);
        b.setMinWidth(dp(96));
        b.setBackground(buttonBg(primary));
        return b;
    }

    Drawable buttonBg(boolean primary) {
        return primary ? pressable(C_ACCENT, C_ACCENT_DOWN, 0xFF2E3B35, dp(3))
                       : pressable(C_BUTTON, C_BUTTON_DOWN, C_BUTTON, dp(3));
    }

    void setEnabled(TextView b, boolean on) {
        b.setEnabled(on);
        b.setTextColor(on ? C_TEXT : 0xFF6E7A74);
    }

    static GradientDrawable rounded(int color, int radius, int stroke) {
        GradientDrawable d = new GradientDrawable();
        d.setColor(color);
        d.setCornerRadius(radius);
        if (stroke != 0) d.setStroke(Math.max(1, radius / 3), stroke);
        return d;
    }

    static StateListDrawable pressable(int normal, int pressed, int disabled, int radius) {
        StateListDrawable s = new StateListDrawable();
        s.addState(new int[]{-android.R.attr.state_enabled}, rounded(disabled, radius, 0));
        s.addState(new int[]{android.R.attr.state_pressed}, rounded(pressed, radius, 0));
        s.addState(new int[]{}, rounded(normal, radius, 0));
        return s;
    }

    static String size(long bytes) {
        if (bytes >= 1024 * 1024) return String.format(Locale.ROOT, "%.1f MB", bytes / 1048576.0);
        return (bytes / 1024) + " KB";
    }

    void hideKeyboard() {
        View f = getCurrentFocus();
        if (f == null) return;
        android.view.inputmethod.InputMethodManager imm =
                (android.view.inputmethod.InputMethodManager) getSystemService(INPUT_METHOD_SERVICE);
        if (imm != null) imm.hideSoftInputFromWindow(f.getWindowToken(), 0);
        f.clearFocus();
    }

    void toast(String s) { Toast.makeText(this, s, Toast.LENGTH_LONG).show(); }
}
