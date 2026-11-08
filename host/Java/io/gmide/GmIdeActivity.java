package io.gmide;

import android.content.Intent;
import android.content.pm.ActivityInfo;
import android.os.Build;
import android.os.Bundle;
import android.view.View;
import android.view.Window;
import android.view.WindowManager;

import org.libsdl.app.SDLActivity;

// Activity principal: o SDL cria a superficie GL e chama SDL_main() em
// libgmhost.so, que repassa para o lancador em C# (Launcher.Main).
//
// Tela: sempre em paisagem, tela cheia imersiva (sem barra de status/navegacao),
// sem barra de titulo (tema NoTitleBar no manifesto) e usando a area do recorte
// da camera.
public class GmIdeActivity extends SDLActivity {
    @Override
    protected String[] getLibraries() {
        // A ultima da lista e a que contem SDL_main.
        return new String[] { "SDL2", "gmhost" };
    }

    // Argumentos repassados para a IDE (como "GameMaker.exe projeto.yyp" no PC).
    // Ex.: adb shell am start -n io.gmide.android/io.gmide.GmIdeActivity --es project /caminho/Projeto.yyp
    @Override
    protected String[] getArguments() {
        Intent i = getIntent();
        String project = i != null ? i.getStringExtra("project") : null;
        return project != null && project.length() > 0 ? new String[] { project } : new String[0];
    }

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        Window w = getWindow();
        w.addFlags(WindowManager.LayoutParams.FLAG_FULLSCREEN | WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        if (Build.VERSION.SDK_INT >= 28) {
            // NEVER: a IDE nao desenha sob o recorte da camera; o Android poe uma faixa
            // preta desse lado (cantos dificeis de tocar ficam fora da area util).
            WindowManager.LayoutParams lp = w.getAttributes();
            lp.layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_NEVER;
            w.setAttributes(lp);
        }
        setRequestedOrientation(ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE);
        super.onCreate(savedInstanceState);
        applySafeMargin();
        ShortcutBar.install(this);   // barra de atalhos (Esc, Ctrl/Shift/Alt, clique direito, teclado...)
        followKeyboard();
        hideSystemUi();
        handlePreviewAction(getIntent());
    }

    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        handlePreviewAction(intent);
    }

    private void handlePreviewAction(Intent intent) {
        String action = intent == null ? null : intent.getStringExtra("gmide.preview_action");
        if (action == null) return;
        intent.removeExtra("gmide.preview_action");
        if ("stop".equals(action)) ShortcutBar.postCommand("preview-stop");
    }

    // Teclado na tela, dois modos escolhidos pelo C# (KeyboardMode.cs) conforme o
    // gadget focado, antes de o teclado abrir:
    //  - editor de codigo (sShrink = true): ADJUST_NOTHING + padding inferior do
    //    mLayout igual a altura do teclado. A SDLSurface encolhe (a IDE recebe um
    //    resize) e o C# deixa so o editor visivel nessa faixa (esconde menus,
    //    barra de ferramentas e docks; estica a janela do editor no workspace).
    //  - outros campos (caixas de busca, inspetor...): ADJUST_PAN, sem padding.
    //    O Android empurra a imagem para cima ate o campo (o SDL poe a view de
    //    texto no retangulo do SDL_SetTextInputRect) e a IDE nao muda de tamanho.
    // sImeBottom: altura do teclado em px (0 = fechado), lida pelo C# a cada quadro.
    private int mSafePx;
    private static GmIdeActivity sSelf;
    private static volatile boolean sShrink = false;
    public static volatile int sImeBottom = 0;

    private void followKeyboard() {
        sSelf = this;
        if (mLayout == null || Build.VERSION.SDK_INT < 30) {
            // Sem WindowInsets.Type.ime(): so da para panar.
            getWindow().setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_PAN);
            return;
        }
        applyImeMode();
        final View root = getWindow().getDecorView();
        root.setOnApplyWindowInsetsListener(new View.OnApplyWindowInsetsListener() {
            @Override
            public android.view.WindowInsets onApplyWindowInsets(View v, android.view.WindowInsets insets) {
                int ime = insets.getInsets(android.view.WindowInsets.Type.ime()).bottom;
                sImeBottom = ime;
                applyBottomPadding();
                placeEcho();
                placeCodeBar();
                return v.onApplyWindowInsets(insets);
            }
        });
    }

    private void applyImeMode() {
        // Os dois modos usam ADJUST_NOTHING: a imagem da IDE nao e mais empurrada para cima (isso
        // deslocava a interface inteira). Campos comuns mostram o texto numa barra sobre o teclado
        // (setImeEcho); o editor de codigo encolhe a superficie (padding acima) e ganha a barra de
        // simbolos. O tipo de entrada do IME (codigo: sem correcao nem maiusculas) e escolhido pelo C#
        // (KeyboardMode.SendImeCode -> SDLActivity.gmSetImeCode), independente deste modo.
        getWindow().setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_NOTHING);
        View root = getWindow().getDecorView();
        if (root != null) root.requestApplyInsets();
        placeCodeBar();
    }

    // Superficie da IDE acima do teclado (+ barra de simbolos) no modo editor de codigo.
    private void applyBottomPadding() {
        if (mLayout == null) return;
        int ime = sImeBottom;
        int bottom = mSafePx;
        if (sShrink && ime > 0) bottom = Math.max(mSafePx, ime + codeBarHeight());
        if (mLayout.getPaddingBottom() != bottom)
            mLayout.setPadding(mSafePx, mSafePx, mSafePx, bottom);
    }

    // ---- barra de simbolos do editor de codigo (estilo dos editores de codigo mobile)
    //
    // Fica colada no teclado enquanto o editor de codigo esta focado. Teclas de texto entram pelo mesmo
    // caminho do teclado virtual (SDLActivity.gmCommitText); Tab/setas/Desfazer/Refazer vao como tecla
    // fisica, igual a barra de atalhos (ShortcutBar). Pares como ( ) ficam juntos para economizar toques.

    private android.widget.HorizontalScrollView mCodeBar;
    private static final String[][] CODE_KEYS = {
        {"Tab", "\t"}, {"←", "L"}, {"→", "R"}, {"↑", "U"}, {"↓", "D"},
        {"{", "{"}, {"}", "}"}, {"(", "("}, {")", ")"}, {"[", "["}, {"]", "]"},
        {";", ";"}, {"=", "="}, {"\"", "\""}, {"_", "_"}, {".", "."}, {",", ","},
        {"+", "+"}, {"-", "-"}, {"*", "*"}, {"/", "/"}, {"<", "<"}, {">", ">"},
        {"!", "!"}, {"&", "&"}, {"|", "|"}, {":", ":"}, {"?", "?"}, {"'", "'"}, {"#", "#"}, {"$", "$"},
        {"Desf.", "Z"}, {"Refaz.", "Y"},
    };

    private int codeBarHeight() {
        if (mCodeBar == null || mCodeBar.getVisibility() != View.VISIBLE) return 0;
        int h = mCodeBar.getHeight();
        return h > 0 ? h : Math.round(38 * getResources().getDisplayMetrics().density);
    }

    private void buildCodeBar() {
        float d = getResources().getDisplayMetrics().density;
        mCodeBar = new android.widget.HorizontalScrollView(this);
        mCodeBar.setHorizontalScrollBarEnabled(false);
        mCodeBar.setBackgroundColor(0xFF1E1E1E);
        android.widget.LinearLayout row = new android.widget.LinearLayout(this);
        row.setOrientation(android.widget.LinearLayout.HORIZONTAL);
        int pad = Math.round(2 * d);
        row.setPadding(pad, pad, pad, pad);
        for (final String[] k : CODE_KEYS) {
            final android.widget.TextView b = new android.widget.TextView(this);
            b.setText(k[0]);
            b.setTextColor(0xFFEFEFEF);
            b.setTextSize(android.util.TypedValue.COMPLEX_UNIT_SP, 18);
            b.setGravity(android.view.Gravity.CENTER);
            b.setMinWidth(Math.round(44 * d));
            b.setPadding(Math.round(10 * d), 0, Math.round(10 * d), 0);
            android.graphics.drawable.GradientDrawable bg = new android.graphics.drawable.GradientDrawable();
            bg.setColor(0xFF333333);
            bg.setCornerRadius(6 * d);
            b.setBackground(bg);
            android.widget.LinearLayout.LayoutParams lp = new android.widget.LinearLayout.LayoutParams(
                android.view.ViewGroup.LayoutParams.WRAP_CONTENT, Math.round(34 * d));
            lp.setMargins(pad, 0, pad, 0);
            b.setLayoutParams(lp);
            b.setFocusable(false);
            b.setOnClickListener(new View.OnClickListener() {
                @Override
                public void onClick(View v) {
                    v.performHapticFeedback(android.view.HapticFeedbackConstants.KEYBOARD_TAP);
                    codeKey(k[1]);
                }
            });
            row.addView(b);
        }
        mCodeBar.addView(row);
        mCodeBar.setFocusable(false);
        android.widget.FrameLayout content = (android.widget.FrameLayout) findViewById(android.R.id.content);
        android.widget.FrameLayout.LayoutParams flp = new android.widget.FrameLayout.LayoutParams(
            android.view.ViewGroup.LayoutParams.MATCH_PARENT, android.view.ViewGroup.LayoutParams.WRAP_CONTENT,
            android.view.Gravity.BOTTOM);
        flp.bottomMargin = sImeBottom;
        content.addView(mCodeBar, flp);
    }

    private static void codeKey(String k) {
        int code;
        switch (k) {
            case "\t": code = android.view.KeyEvent.KEYCODE_TAB; break;
            case "L": code = android.view.KeyEvent.KEYCODE_DPAD_LEFT; break;
            case "R": code = android.view.KeyEvent.KEYCODE_DPAD_RIGHT; break;
            case "U": code = android.view.KeyEvent.KEYCODE_DPAD_UP; break;
            case "D": code = android.view.KeyEvent.KEYCODE_DPAD_DOWN; break;
            case "Z": case "Y": {
                int key = "Z".equals(k) ? android.view.KeyEvent.KEYCODE_Z : android.view.KeyEvent.KEYCODE_Y;
                onNativeKeyDown(android.view.KeyEvent.KEYCODE_CTRL_LEFT);
                onNativeKeyDown(key);
                onNativeKeyUp(key);
                onNativeKeyUp(android.view.KeyEvent.KEYCODE_CTRL_LEFT);
                return;
            }
            default:
                org.libsdl.app.SDLActivity.gmCommitText(k);
                return;
        }
        onNativeKeyDown(code);
        onNativeKeyUp(code);
    }

    // A barra fica na camada de conteudo da janela (android.R.id.content, FrameLayout), fora do mLayout:
    // o padding inferior do mLayout (teclado + barra) encolhe a superficie da IDE e a barra ocupa
    // exatamente a faixa entre a superficie e o teclado (margem inferior = altura do teclado).
    private void placeCodeBar() {
        if (mLayout == null) return;
        boolean show = sShrink && sImeBottom > 0;
        if (!show) {
            if (mCodeBar != null && mCodeBar.getVisibility() != View.GONE) {
                mCodeBar.setVisibility(View.GONE);
                applyBottomPadding();
            }
            return;
        }
        if (mCodeBar == null) buildCodeBar();
        android.widget.FrameLayout.LayoutParams lp = (android.widget.FrameLayout.LayoutParams) mCodeBar.getLayoutParams();
        if (lp.bottomMargin != sImeBottom) { lp.bottomMargin = sImeBottom; mCodeBar.setLayoutParams(lp); }
        boolean wasHidden = mCodeBar.getVisibility() != View.VISIBLE;
        mCodeBar.setVisibility(View.VISIBLE);
        mCodeBar.bringToFront();
        if (wasHidden) {
            // Altura real so depois do layout: reaplica o padding da superficie.
            mCodeBar.post(new Runnable() {
                @Override
                public void run() { applyBottomPadding(); }
            });
        }
    }

    // ---- barra de texto sobre o teclado (campos comuns; estilo dos editores mobile)

    private android.widget.TextView mEcho;
    private static volatile String sEchoText = null;

    /** Chamado pelo C# (KeyboardMode, thread do SDL): texto do campo focado; null esconde. */
    public static void setImeEcho(final String text) {
        sEchoText = text;
        final GmIdeActivity a = sSelf;
        if (a == null) return;
        a.runOnUiThread(new Runnable() {
            @Override
            public void run() { a.updateEcho(); }
        });
    }

    private void updateEcho() {
        if (mLayout == null) return;
        String t = sEchoText;
        if (mEcho == null) {
            if (t == null) return;
            float d = getResources().getDisplayMetrics().density;
            mEcho = new android.widget.TextView(this);
            mEcho.setTextColor(0xFFEFEFEF);
            mEcho.setTextSize(android.util.TypedValue.COMPLEX_UNIT_SP, 18);
            mEcho.setSingleLine(true);
            mEcho.setEllipsize(android.text.TextUtils.TruncateAt.START); // fim do texto (onde se digita) sempre visivel
            int p = Math.round(12 * d);
            mEcho.setPadding(p, Math.round(8 * d), p, Math.round(8 * d));
            android.graphics.drawable.GradientDrawable bg = new android.graphics.drawable.GradientDrawable();
            bg.setColor(0xF0262626);
            bg.setStroke(Math.max(1, Math.round(d)), 0xFF039D5B);
            mEcho.setBackground(bg);
            mEcho.setFocusable(false);
            mEcho.setClickable(false);
            mLayout.addView(mEcho);
        }
        if (t == null || sImeBottom <= 0) { mEcho.setVisibility(View.GONE); return; }
        mEcho.setText(t + "▏");
        mEcho.setVisibility(View.VISIBLE);
        mEcho.bringToFront();
        placeEcho();
    }

    private void placeEcho() {
        if (mEcho == null) return;
        if (sEchoText == null || sImeBottom <= 0) { mEcho.setVisibility(View.GONE); return; }
        android.view.ViewGroup.LayoutParams lp0 = mEcho.getLayoutParams();
        android.widget.RelativeLayout.LayoutParams lp = lp0 instanceof android.widget.RelativeLayout.LayoutParams
            ? (android.widget.RelativeLayout.LayoutParams) lp0
            : new android.widget.RelativeLayout.LayoutParams(android.view.ViewGroup.LayoutParams.MATCH_PARENT,
                                                              android.view.ViewGroup.LayoutParams.WRAP_CONTENT);
        lp.width = android.view.ViewGroup.LayoutParams.MATCH_PARENT;
        lp.addRule(android.widget.RelativeLayout.ALIGN_PARENT_BOTTOM);
        lp.bottomMargin = Math.max(0, sImeBottom - mLayout.getPaddingBottom());
        mEcho.setLayoutParams(lp);
        mEcho.setVisibility(View.VISIBLE);
    }

    /** Vibracao curta de confirmacao (toque longo armado). Qualquer thread. */
    public static void haptic() {
        final GmIdeActivity a = sSelf;
        if (a == null) return;
        a.runOnUiThread(new Runnable() {
            @Override
            public void run() {
                View v = a.getWindow().getDecorView();
                if (v != null) v.performHapticFeedback(android.view.HapticFeedbackConstants.LONG_PRESS);
            }
        });
    }

    // Chamado pelo C# (JNI, thread do SDL) quando o foco muda para um campo de texto.
    public static void setImeShrink(boolean shrink) {
        if (sShrink == shrink) return;
        sShrink = shrink;
        final GmIdeActivity a = sSelf;
        if (a == null || Build.VERSION.SDK_INT < 30) return;
        a.runOnUiThread(new Runnable() {
            @Override
            public void run() { a.applyImeMode(); }
        });
    }

    // A faixa preta do lado do recorte da camera ja vem do modo NEVER acima (como
    // em outros apps). Margem extra em volta: padrao 0 (desligada), ajustavel em
    // SharedPreferences "gmide" / "safe_margin_dp" se algum aparelho precisar.
    private void applySafeMargin() {
        if (mLayout == null) return;
        int dp = getSharedPreferences("gmide", MODE_PRIVATE).getInt("safe_margin_dp", 0);
        int px = Math.round(dp * getResources().getDisplayMetrics().density);
        mLayout.setBackgroundColor(0xFF000000);
        mSafePx = px;
        mLayout.setPadding(px, px, px, px);
    }

    // O SDL escolhe a orientacao pelo formato da janela (mais alta que larga ->
    // retrato). A IDE e um app de mesa: fica sempre em paisagem.
    @Override
    public void setOrientationBis(int w, int h, boolean resizable, String hint) {
        setRequestedOrientation(ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE);
    }

    @Override
    public void onWindowFocusChanged(boolean hasFocus) {
        super.onWindowFocusChanged(hasFocus);
        if (hasFocus) hideSystemUi();
    }

    @SuppressWarnings("deprecation")
    private void hideSystemUi() {
        getWindow().getDecorView().setSystemUiVisibility(
            View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
            | View.SYSTEM_UI_FLAG_LAYOUT_STABLE
            | View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION
            | View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN
            | View.SYSTEM_UI_FLAG_HIDE_NAVIGATION
            | View.SYSTEM_UI_FLAG_FULLSCREEN);
    }

    // Resultado dos seletores do Storage Access Framework (ver FilePicker).
    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        if (FilePicker.onActivityResult(requestCode, resultCode, data)) return;
        super.onActivityResult(requestCode, resultCode, data);
    }
}
