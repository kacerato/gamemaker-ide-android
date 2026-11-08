package io.gmide;

import android.app.Activity;
import android.content.SharedPreferences;
import android.content.res.ColorStateList;
import android.graphics.drawable.Drawable;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.RippleDrawable;
import android.os.Build;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import android.util.DisplayMetrics;
import android.util.Log;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.KeyEvent;
import android.view.Menu;
import android.view.MenuItem;
import android.view.MotionEvent;
import android.view.View;
import android.view.ViewConfiguration;
import android.view.ViewGroup;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.PopupMenu;
import android.widget.RelativeLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;

import org.libsdl.app.SDLActivity;

import java.util.ArrayDeque;

// Atalhos na tela para usar a IDE so com toque (docs/touch-ui.md).
//
// Apresentacao: um botao redondo flutuante (44dp, semitransparente) por cima da
// IDE. Arrastar com o dedo move o botao (ao soltar ele gruda na borda esquerda ou
// direita mais proxima); tocar abre um painel com os atalhos perto do botao.
// Tocar fora do painel ou no botao de novo fecha. A superficie do SDL ocupa a
// tela inteira (nada de reservar espaco). Ctrl/Shift/Alt presos e o modo de
// clique direito aparecem num selo no botao enquanto o painel esta fechado.
// Nenhuma view aqui pega foco: o teclado continua indo para a IDE.
//
// As teclas vao para o SDL como teclado fisico (SDLActivity.onNativeKeyDown/Up
// com keycodes do Android): o SDL atualiza o estado do teclado e a IDE recebe
// SDL_KEYDOWN/UP normais. Ctrl/Shift/Alt "presos" ficam realmente pressionados
// para o SDL, entao valem tambem para cliques (Ctrl+clique, Shift+clique) e
// para a roda (Ctrl+roda = zoom).
//
// Canal com o C# (host/TouchInput.cs): a barra enfileira um comando de texto e
// manda a tecla HELP (AKEYCODE_HELP -> SDL_SCANCODE_HELP, que a IDE nao usa). O
// filtro de eventos em C# ve o HELP e chama takeCommand() por JNI. No sentido
// contrario o C# chama onKeyUsed / onModifierKeyUp / onRightClickUsed.
//
// Preferencias ("gmide"):
//   shortcut_bar_position  "float" (padrao, botao flutuante) | "off". Valores
//                          antigos ("bottom"/"side") viram "float" no install;
//                          o C# (IdeSetup.ComputeAutoPct) so desconta espaco
//                          para "bottom"/"side", entao "float" = tela inteira.
//   shortcut_float_x       float 0..1: posicao horizontal do botao (0 = borda
//                          esquerda, 1 = direita) dentro da area livre
//   shortcut_float_y       float 0..1: idem vertical (area acima do teclado)
//   ui_scale_percent       int, 0 = automatico (lido em C# no boot / ao vivo)
//   ui_scale_effective     int, gravado pelo C#: escala em uso
//
// Uso: em GmIdeActivity.onCreate, depois de super.onCreate(...):
//     ShortcutBar.install(this);
public final class ShortcutBar {
    private static final String TAG = "GmIde.Bar";
    public static final String PREFS = "gmide";
    public static final String KEY_POSITION = "shortcut_bar_position";
    public static final String KEY_FLOAT_X = "shortcut_float_x";
    public static final String KEY_FLOAT_Y = "shortcut_float_y";
    public static final String KEY_SCALE = "ui_scale_percent";
    public static final String KEY_SCALE_EFFECTIVE = "ui_scale_effective";

    private static final int FAB_DP = 44;          // botao flutuante
    private static final int FAB_BOX_DP = 50;      // caixa do botao (sobra para o selo)
    private static final int ROW_DP = 40;          // altura dos botoes do painel
    private static final int BUTTON_MIN_DP = 48;
    private static final int GAP_DP = 6;           // distancia painel <-> botao
    private static final long DOUBLE_TAP_MS = 350;
    private static final long REPEAT_DELAY_MS = 450, REPEAT_RATE_MS = 60;

    private static final int MOD_CTRL = 0, MOD_SHIFT = 1, MOD_ALT = 2;
    private static final int[] MOD_KEYCODES = {
        KeyEvent.KEYCODE_CTRL_LEFT, KeyEvent.KEYCODE_SHIFT_LEFT, KeyEvent.KEYCODE_ALT_LEFT };
    private static final String[] MOD_NAMES = { "Ctrl", "Shift", "Alt" };
    private static final String[] MOD_BADGE = { "C", "S", "A" };
    private static final int OFF = 0, ONESHOT = 1, LOCKED = 2;

    private static final int COLOR_PANEL = 0xEB1E1F24;
    private static final int COLOR_FAB = 0xA62D2F36;
    private static final int COLOR_FAB_OPEN = 0xE62F6FD6;
    private static final int COLOR_BUTTON = 0xFF2D2F36;
    private static final int COLOR_ONESHOT = 0xFF2F6FD6;
    private static final int COLOR_LOCKED = 0xFFD9822B;
    private static final int COLOR_TEXT = 0xFFE8E8E8;

    private static Activity sActivity;
    private static Handler sMain;
    private static ViewGroup sRoot;          // mLayout do SDLActivity (RelativeLayout)
    private static FrameLayout sFab;         // botao flutuante (caixa)
    private static TextView sFabCircle, sBadge;
    private static View sScrim, sPanel;
    private static boolean sPanelOpen;
    private static boolean sPreview;
    private static float sFx = 1f, sFy = 0.5f; // posicao salva (fracao da area livre)
    private static float sDensity = 1f;

    private static final int[] sMod = new int[3];
    private static final long[] sModTap = new long[3];
    private static final TextView[] sModButtons = new TextView[3];
    private static int sRightClick = OFF;
    private static long sRightTap;
    private static TextView sRightButton;

    // true enquanto a propria barra esta mandando teclas (o C# e chamado de volta
    // de dentro do onNativeKeyDown, na mesma thread) -> nao tratar como "uso".
    private static boolean sSending;
    private static final ArrayDeque<String> sCommands = new ArrayDeque<String>();
    private static boolean sNativeBroken;

    private ShortcutBar() { }

    // ------------------------------------------------------------ instalacao

    public static void install(Activity activity) {
        install(activity, false);
    }

    // Separate process, same movable control and placement rules; no SDL calls in the runner.
    public static void installPreview(Activity activity) {
        install(activity, true);
    }

    private static void install(Activity activity, boolean preview) {
        try {
            sActivity = activity;
            sPreview = preview;
            sMain = new Handler(Looper.getMainLooper());
            View content;
            if (preview) {
                RelativeLayout overlay = new RelativeLayout(activity);
                activity.addContentView(overlay, new ViewGroup.LayoutParams(-1, -1));
                content = overlay;
            } else content = SDLActivity.getContentView();
            if (!(content instanceof ViewGroup)) {
                Log.w(TAG, "install: SDLActivity.getContentView() = " + content + "; atalhos nao instalados");
                return;
            }
            sRoot = (ViewGroup) content;
            DisplayMetrics dm = activity.getResources().getDisplayMetrics();
            sDensity = dm.density;
            for (int i = 0; i < 3; i++) { sMod[i] = OFF; sModButtons[i] = null; }
            sRightClick = OFF;
            sRightButton = null;
            sPanelOpen = false;
            SharedPreferences p = prefs();
            String pos = p.getString(KEY_POSITION, "float");
            if (!"off".equals(pos) && !"float".equals(pos)) {
                // Barra fixa antiga (rodape/lateral): migra para o botao flutuante. Tambem faz
                // o C# parar de descontar a barra na escala automatica.
                Log.i(TAG, "shortcut_bar_position=" + pos + " (barra fixa antiga) -> float");
                pos = "float";
                p.edit().putString(KEY_POSITION, pos).apply();
            }
            sFx = clamp01(p.getFloat(KEY_FLOAT_X, 1f));
            sFy = clamp01(p.getFloat(KEY_FLOAT_Y, 0.5f));
            Log.i(TAG, "install: posicao=" + pos + " botao=(" + sFx + ", " + sFy + ") density=" + sDensity
                + " tela=" + dm.widthPixels + "x" + dm.heightPixels
                + " escala(prefs)=" + p.getInt(KEY_SCALE, 0) + "% efetiva=" + p.getInt(KEY_SCALE_EFFECTIVE, 0) + "%");
            if ("off".equals(pos) && !preview) {
                Log.i(TAG, "atalhos desligados (shortcut_bar_position=off)");
                return;
            }
            build();
            // mLayout muda de tamanho/padding (teclado na tela, rotacao, recorte): reposiciona
            // o botao e o painel dentro da area livre (acima do teclado).
            sRoot.addOnLayoutChangeListener(new View.OnLayoutChangeListener() {
                @Override
                public void onLayoutChange(View v, int l, int t, int r, int b, int ol, int ot, int or, int ob) {
                    placeFab();
                    if (sPanelOpen) placePanel();
                }
            });
        } catch (Throwable t) {
            Log.e(TAG, "install falhou", t);
        }
    }

    private static SharedPreferences prefs() {
        return sActivity.getSharedPreferences(PREFS, Activity.MODE_PRIVATE);
    }

    private static int dp(float v) { return Math.round(v * sDensity); }

    private static float clamp01(float v) { return v != v ? 0.5f : Math.max(0f, Math.min(1f, v)); }

    // Area livre para os filhos de mLayout: dentro do padding (o padding inferior e a
    // altura do teclado, ver GmIdeActivity.followKeyboard).
    private static int areaW() { return Math.max(0, sRoot.getWidth() - sRoot.getPaddingLeft() - sRoot.getPaddingRight()); }
    private static int areaH() { return Math.max(0, sRoot.getHeight() - sRoot.getPaddingTop() - sRoot.getPaddingBottom()); }

    // ------------------------------------------------------------ views

    // Ordem de desenho/toque: o SDL pode adicionar views ao mLayout depois (DummyEdit
    // do texto). translationZ alto mantem scrim < painel < botao por cima de tudo,
    // tanto no desenho quanto na entrega de toques.
    private static void build() {
        // Scrim: tela inteira transparente sob o painel; tocar nele fecha o painel e o
        // toque NAO vai para a IDE.
        View scrim = new View(sActivity);
        scrim.setFocusable(false);
        scrim.setVisibility(View.GONE);
        scrim.setOnTouchListener(new View.OnTouchListener() {
            public boolean onTouch(View v, MotionEvent e) {
                if (e.getActionMasked() == MotionEvent.ACTION_DOWN) closePanel("toque fora");
                return true;
            }
        });
        scrim.setTranslationZ(dp(10));
        sScrim = scrim;
        sRoot.addView(scrim, new RelativeLayout.LayoutParams(
            ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));

        View panel = buildPanel();
        panel.setVisibility(View.GONE);
        panel.setTranslationZ(dp(11));
        sPanel = panel;
        RelativeLayout.LayoutParams plp = new RelativeLayout.LayoutParams(
            ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        plp.addRule(RelativeLayout.ALIGN_PARENT_LEFT);
        plp.addRule(RelativeLayout.ALIGN_PARENT_TOP);
        sRoot.addView(panel, plp);

        FrameLayout fab = buildFab();
        fab.setTranslationZ(dp(12));
        sFab = fab;
        RelativeLayout.LayoutParams flp = new RelativeLayout.LayoutParams(dp(FAB_BOX_DP), dp(FAB_BOX_DP));
        flp.addRule(RelativeLayout.ALIGN_PARENT_LEFT);
        flp.addRule(RelativeLayout.ALIGN_PARENT_TOP);
        sRoot.addView(fab, flp);

        updateModButtons();
        updateRightButton();
        placeFab();
        Log.i(TAG, "botao flutuante instalado (" + dp(FAB_DP) + " px)");
    }

    private static FrameLayout buildFab() {
        FrameLayout box = new FrameLayout(sActivity);
        box.setFocusable(false);
        box.setClipChildren(false);
        box.setClipToPadding(false);

        TextView c = new TextView(sActivity);
        c.setText("☰");
        c.setTextColor(COLOR_TEXT);
        c.setTextSize(TypedValue.COMPLEX_UNIT_SP, 18);
        c.setGravity(Gravity.CENTER);
        c.setFocusable(false);
        c.setDuplicateParentStateEnabled(true); // ripple ao tocar a caixa
        c.setContentDescription("Atalhos (arrastar para mover)");
        sFabCircle = c;
        setFabColor(false);
        box.addView(c, new FrameLayout.LayoutParams(dp(FAB_DP), dp(FAB_DP), Gravity.CENTER));

        // Selo dos modificadores presos (C/S/A) e do modo clique direito (D).
        TextView b = new TextView(sActivity);
        b.setTextColor(0xFFFFFFFF);
        b.setTextSize(TypedValue.COMPLEX_UNIT_SP, 9);
        b.setGravity(Gravity.CENTER);
        b.setSingleLine(true);
        b.setIncludeFontPadding(false);
        b.setMinWidth(dp(16));
        b.setPadding(dp(3), 0, dp(3), 0);
        b.setFocusable(false);
        b.setVisibility(View.GONE);
        sBadge = b;
        box.addView(b, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(16), Gravity.TOP | Gravity.RIGHT));

        final int slop = ViewConfiguration.get(sActivity).getScaledTouchSlop();
        box.setOnTouchListener(new View.OnTouchListener() {
            float downX, downY, startTx, startTy;
            boolean dragging;
            public boolean onTouch(View v, MotionEvent e) {
                switch (e.getActionMasked()) {
                    case MotionEvent.ACTION_DOWN:
                        downX = e.getRawX(); downY = e.getRawY();
                        startTx = v.getTranslationX(); startTy = v.getTranslationY();
                        dragging = false;
                        v.setPressed(true);
                        return true;
                    case MotionEvent.ACTION_MOVE: {
                        float dx = e.getRawX() - downX, dy = e.getRawY() - downY;
                        if (!dragging && dx * dx + dy * dy > slop * slop) {
                            dragging = true;
                            v.setPressed(false);
                            if (sPanelOpen) closePanel("arrastando o botao");
                        }
                        if (dragging) {
                            v.setTranslationX(clampX(startTx + dx));
                            v.setTranslationY(clampY(startTy + dy));
                            refreshHole(v);
                        }
                        return true;
                    }
                    case MotionEvent.ACTION_UP:
                        v.setPressed(false);
                        if (dragging) dropFab(v);
                        else togglePanel();
                        return true;
                    case MotionEvent.ACTION_CANCEL:
                        v.setPressed(false);
                        if (dragging) dropFab(v);
                        return true;
                }
                return true;
            }
        });
        return box;
    }

    private static void setFabColor(boolean open) {
        GradientDrawable bg = new GradientDrawable();
        bg.setShape(GradientDrawable.OVAL);
        bg.setColor(open ? COLOR_FAB_OPEN : COLOR_FAB);
        bg.setStroke(Math.max(1, dp(1)), 0x66FFFFFF);
        sFabCircle.setBackground(new RippleDrawable(ColorStateList.valueOf(0x40FFFFFF), bg, null));
    }

    private static int maxX() { return Math.max(0, areaW() - dp(FAB_BOX_DP)); }
    private static int maxY() { return Math.max(0, areaH() - dp(FAB_BOX_DP)); }
    private static float clampX(float x) { return Math.max(0, Math.min(maxX(), x)); }
    private static float clampY(float y) { return Math.max(0, Math.min(maxY(), y)); }

    // Soltou o arrasto: gruda na borda esquerda/direita mais proxima e salva.
    private static void dropFab(View v) {
        int mx = maxX(), my = maxY();
        float x = v.getTranslationX();
        sFx = mx > 0 && x > mx / 2f ? 1f : 0f;
        sFy = my > 0 ? clamp01(v.getTranslationY() / my) : 0.5f;
        prefs().edit().putFloat(KEY_FLOAT_X, sFx).putFloat(KEY_FLOAT_Y, sFy).apply();
        final View fv = v;
        v.animate().translationX(sFx * mx).setDuration(120).withEndAction(new Runnable() {
            public void run() { refreshHole(fv); }
        }).start();
        Log.i(TAG, "botao solto em (" + sFx + ", " + sFy + ")");
    }

    // A SDLSurface e um SurfaceView: a janela recorta um "buraco" para ela, calculado so no
    // layout. Mudar translation nao refaz o layout, entao o buraco antigo cobria o botao na
    // posicao nova (botao invisivel ate o proximo layout). Pede o recalculo do buraco.
    private static void refreshHole(View v) {
        v.invalidate();
        if (v.getParent() != null) v.getParent().requestTransparentRegion(v);
    }

    // Posicao salva -> pixels da area livre atual (com teclado a area encolhe e o botao
    // continua visivel acima dele).
    private static void placeFab() {
        if (sFab == null || sRoot.getWidth() <= 0) return;
        sFab.animate().cancel();
        sFab.setTranslationX(Math.round(sFx * maxX()));
        sFab.setTranslationY(Math.round(sFy * maxY()));
        refreshHole(sFab);
    }

    // ------------------------------------------------------------ painel

    private static View buildPanel() {
        LinearLayout grid = new LinearLayout(sActivity);
        grid.setOrientation(LinearLayout.VERTICAL);
        grid.setPadding(dp(4), dp(4), dp(4), dp(4));

        if (sPreview) {
            LinearLayout actions = row(grid);
            TextView stop = makeButton("■ Parar", "Parar o preview e voltar a IDE");
            setColor(stop, 0xFF833B39);
            stop.setOnClickListener(new View.OnClickListener() { public void onClick(View v) { returnToIde("stop"); } });
            add(actions, stop);
            TextView ide = makeButton("IDE / Depurador", "Voltar ao projeto sem encerrar o jogo");
            ide.setOnClickListener(new View.OnClickListener() { public void onClick(View v) { returnToIde("inspect"); } });
            add(actions, ide);
            grid.setBackgroundColor(COLOR_PANEL);
            return grid;
        }

        LinearLayout r1 = row(grid);
        add(r1, keyButton("Esc", "Esc", KeyEvent.KEYCODE_ESCAPE));
        add(r1, keyButton("Tab", "Tab", KeyEvent.KEYCODE_TAB));
        for (int i = 0; i < 3; i++) {
            final int m = i;
            TextView b = makeButton(MOD_NAMES[i], MOD_NAMES[i] + ": toque = proxima tecla/clique, toque duplo = travar");
            b.setOnClickListener(new View.OnClickListener() {
                public void onClick(View v) { tapModifier(m); }
            });
            sModButtons[i] = b;
            add(r1, b);
        }
        TextView rc = makeButton("Btn D", "Proximo toque = clique direito (toque duplo = travar)");
        rc.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { tapRightClick(); }
        });
        sRightButton = rc;
        add(r1, rc);

        LinearLayout r2 = row(grid);
        add(r2, repeatButton("←", "Seta esquerda", KeyEvent.KEYCODE_DPAD_LEFT));
        add(r2, repeatButton("↑", "Seta acima", KeyEvent.KEYCODE_DPAD_UP));
        add(r2, repeatButton("↓", "Seta abaixo", KeyEvent.KEYCODE_DPAD_DOWN));
        add(r2, repeatButton("→", "Seta direita", KeyEvent.KEYCODE_DPAD_RIGHT));
        TextView kb = makeButton("⌨", "Mostrar/ocultar teclado");
        kb.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) {
                Log.i(TAG, "botao teclado");
                closePanel("teclado");
                sendCommand("kbd");
            }
        });
        add(r2, kb);
        // Modo Mover: arrastar/pinca movem e aproximam o workspace (TouchInput.PanMode).
        TextView pan = makeButton("Mover", "Arrastar move a area de trabalho e a pinca da zoom (toque de novo para sair)");
        pan.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) {
                sPanMode = !sPanMode;
                Log.i(TAG, "modo mover: " + sPanMode);
                updatePanButton();
                sendCommand("pan:" + (sPanMode ? 1 : 0));
            }
        });
        sPanButton = pan;
        updatePanButton();
        add(r2, pan);
        TextView more = makeButton("⋯", "Mais teclas e opcoes");
        more.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { showMenu(v); }
        });
        add(r2, more);

        LinearLayout r3 = row(grid);
        add(r3, comboButton("Desf.", "Desfazer (Ctrl+Z)", true, false, KeyEvent.KEYCODE_Z));
        add(r3, comboButton("Refaz.", "Refazer (Ctrl+Y)", true, false, KeyEvent.KEYCODE_Y));
        add(r3, comboButton("Salvar", "Salvar (Ctrl+S)", true, false, KeyEvent.KEYCODE_S));
        TextView run = makeButton("F5", "Executar / continuar (F5)");
        run.setOnClickListener(new View.OnClickListener() { public void onClick(View v) {
            closePanel("F5"); sendCommand("preview-f5");
        } });
        add(r3, run);
        TextView stop = makeButton("■ Parar", "Parar a compilacao ou o preview");
        setColor(stop, 0xFF833B39);
        stop.setOnClickListener(new View.OnClickListener() { public void onClick(View v) { closePanel("parar"); sendCommand("preview-stop"); } });
        add(r3, stop);
        TextView apk = makeButton("APK", "Gerar APK do jogo (nome, pacote, versao...)");
        apk.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) {
                closePanel("apk");
                sendCommand("apk");
            }
        });
        add(r3, apk);

        LinearLayout r4 = row(grid);
        add(r4, comboButton("F6 Depurar", "Compilar e executar com debugger", false, false, KeyEvent.KEYCODE_F6));
        TextView game = makeButton("Ver jogo", "Voltar ao preview em execucao");
        game.setOnClickListener(new View.OnClickListener() { public void onClick(View v) { closePanel("ver jogo"); sendCommand("preview-show"); } });
        add(r4, game);

        // Com o teclado aberto a area pode ser mais baixa que o painel: rola.
        ScrollView sv = new ScrollView(sActivity);
        sv.setFocusable(false);
        sv.setVerticalScrollBarEnabled(false);
        sv.addView(grid, new ViewGroup.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        GradientDrawable bg = new GradientDrawable();
        bg.setColor(COLOR_PANEL);
        bg.setCornerRadius(dp(10));
        bg.setStroke(Math.max(1, dp(1)), 0x33FFFFFF);
        sv.setBackground(bg);
        sv.setClipToOutline(true);
        sv.setClickable(true); // toques nos vaos entre botoes nao passam para a IDE
        return sv;
    }

    private static LinearLayout row(LinearLayout grid) {
        LinearLayout r = new LinearLayout(sActivity);
        r.setOrientation(LinearLayout.HORIZONTAL);
        grid.addView(r, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(ROW_DP + 6)));
        return r;
    }

    private static void add(LinearLayout row, View b) {
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.MATCH_PARENT);
        lp.setMargins(dp(2), dp(3), dp(2), dp(3));
        row.addView(b, lp);
    }

    private static void togglePanel() {
        if (sPanelOpen) closePanel("botao");
        else openPanel();
    }

    private static void openPanel() {
        if (sPanel == null || sPanelOpen) return;
        sPanelOpen = true;
        sScrim.setVisibility(View.VISIBLE);
        sPanel.setVisibility(View.VISIBLE);
        placePanel();
        setFabColor(true);
        updateBadge();
        Log.i(TAG, "painel aberto");
    }

    private static void closePanel(String why) {
        if (sPanel == null || !sPanelOpen) return;
        sPanelOpen = false;
        sPanel.setVisibility(View.GONE);
        sScrim.setVisibility(View.GONE);
        setFabColor(false);
        updateBadge();
        Log.i(TAG, "painel fechado (" + why + ")");
    }

    // Painel ao lado do botao (do lado com mais espaco), centrado na altura dele e
    // preso dentro da area livre. Se nao couber ao lado, vai para cima/baixo.
    private static void placePanel() {
        if (sPanel == null || sRoot.getWidth() <= 0) return;
        int aw = areaW(), ah = areaH();
        sPanel.measure(View.MeasureSpec.makeMeasureSpec(aw, View.MeasureSpec.AT_MOST),
                       View.MeasureSpec.makeMeasureSpec(ah, View.MeasureSpec.AT_MOST));
        int pw = sPanel.getMeasuredWidth(), ph = sPanel.getMeasuredHeight();
        int box = dp(FAB_BOX_DP), gap = dp(GAP_DP);
        float fx = sFab.getTranslationX(), fy = sFab.getTranslationY();
        float x, y;
        float spaceLeft = fx - gap, spaceRight = aw - (fx + box) - gap;
        if (Math.max(spaceLeft, spaceRight) >= pw) {
            x = spaceRight >= pw && (spaceRight >= spaceLeft || spaceLeft < pw) ? fx + box + gap : fx - gap - pw;
            y = fy + box / 2f - ph / 2f;
        } else {
            x = fx + box / 2f - pw / 2f;
            float spaceUp = fy - gap, spaceDown = ah - (fy + box) - gap;
            y = spaceDown >= ph || spaceDown >= spaceUp ? fy + box + gap : fy - gap - ph;
        }
        x = Math.max(0, Math.min(aw - pw, x));
        y = Math.max(0, Math.min(ah - ph, y));
        sPanel.setTranslationX(Math.round(x));
        sPanel.setTranslationY(Math.round(y));
        refreshHole(sPanel);
    }

    // Selo no botao: letras dos modificadores ativos + D (clique direito). Laranja se
    // algum estiver travado, azul se so "proxima tecla".
    private static void updateBadge() {
        if (sBadge == null) return;
        StringBuilder sb = new StringBuilder();
        boolean locked = false;
        for (int i = 0; i < 3; i++) {
            if (sMod[i] != OFF) { sb.append(MOD_BADGE[i]); locked |= sMod[i] == LOCKED; }
        }
        if (sRightClick != OFF) { sb.append('D'); locked |= sRightClick == LOCKED; }
        if (sPanMode) { sb.append('M'); locked = true; } // modo Mover ligado (barra recolhida)
        if (sb.length() == 0) {
            sBadge.setVisibility(View.GONE);
            return;
        }
        sBadge.setText(sb.toString());
        GradientDrawable bg = new GradientDrawable();
        bg.setColor(locked ? COLOR_LOCKED : COLOR_ONESHOT);
        bg.setCornerRadius(dp(8));
        bg.setStroke(Math.max(1, dp(1)), 0xCC000000);
        sBadge.setBackground(bg);
        sBadge.setVisibility(View.VISIBLE);
    }

    private static TextView makeButton(String label, String tip) {
        TextView b = new TextView(sActivity);
        b.setText(label);
        b.setTextColor(COLOR_TEXT);
        b.setTextSize(TypedValue.COMPLEX_UNIT_SP, 13);
        b.setGravity(Gravity.CENTER);
        b.setSingleLine(true);
        b.setMinWidth(dp(BUTTON_MIN_DP));
        b.setMinimumWidth(dp(BUTTON_MIN_DP));
        b.setPadding(dp(8), 0, dp(8), 0);
        b.setClickable(true);
        b.setFocusable(false); // nunca tirar o foco do SDLSurface/DummyEdit
        b.setContentDescription(tip);
        if (Build.VERSION.SDK_INT >= 26) b.setTooltipText(tip);
        setColor(b, COLOR_BUTTON);
        return b;
    }

    private static void setColor(TextView b, int color) {
        GradientDrawable bg = new GradientDrawable();
        bg.setColor(color);
        bg.setCornerRadius(dp(6));
        Drawable d = new RippleDrawable(ColorStateList.valueOf(0x40FFFFFF), bg, null);
        b.setBackground(d);
    }

    private static TextView keyButton(String label, String tip, final int keycode) {
        TextView b = makeButton(label, tip);
        b.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) { pressKey(keycode, label(keycode)); }
        });
        return b;
    }

    private static TextView comboButton(final String label, String tip, final boolean ctrl, final boolean shift, final int keycode) {
        TextView b = makeButton(label, tip);
        b.setOnClickListener(new View.OnClickListener() {
            public void onClick(View v) {
                if (keycode == KeyEvent.KEYCODE_F5 || keycode == KeyEvent.KEYCODE_F6) closePanel(label);
                combo(ctrl, shift, keycode, label);
            }
        });
        return b;
    }

    // Setas: segurar repete (como teclado fisico).
    private static TextView repeatButton(String label, String tip, final int keycode) {
        final TextView b = makeButton(label, tip);
        if (Build.VERSION.SDK_INT >= 26) b.setTooltipText(null);
        final Runnable[] repeat = new Runnable[1];
        repeat[0] = new Runnable() {
            public void run() {
                keyDown(keycode);
                sMain.postDelayed(repeat[0], REPEAT_RATE_MS);
            }
        };
        b.setOnTouchListener(new View.OnTouchListener() {
            public boolean onTouch(View v, MotionEvent e) {
                switch (e.getActionMasked()) {
                    case MotionEvent.ACTION_DOWN:
                        v.setPressed(true);
                        keyDown(keycode);
                        sMain.postDelayed(repeat[0], REPEAT_DELAY_MS);
                        return true;
                    case MotionEvent.ACTION_UP:
                    case MotionEvent.ACTION_CANCEL:
                        v.setPressed(false);
                        sMain.removeCallbacks(repeat[0]);
                        keyUp(keycode);
                        releaseOneShots("seta");
                        return true;
                }
                return true;
            }
        });
        return b;
    }

    private static void showMenu(View anchor) {
        PopupMenu pm = new PopupMenu(sActivity, anchor);
        final Menu m = pm.getMenu();
        final int[][] keys = {
            { KeyEvent.KEYCODE_FORWARD_DEL }, { KeyEvent.KEYCODE_DEL }, { KeyEvent.KEYCODE_MOVE_HOME },
            { KeyEvent.KEYCODE_MOVE_END }, { KeyEvent.KEYCODE_PAGE_UP }, { KeyEvent.KEYCODE_PAGE_DOWN },
            { KeyEvent.KEYCODE_ENTER }, { KeyEvent.KEYCODE_F1 }, { KeyEvent.KEYCODE_F2 },
            { KeyEvent.KEYCODE_F6 }, { KeyEvent.KEYCODE_F12 } };
        final String[] keyNames = { "Delete", "Backspace", "Home", "End", "PgUp", "PgDn", "Enter",
            "F1 (ajuda)", "F2 (renomear)", "F6 (depurar)", "F12" };
        for (int i = 0; i < keys.length; i++) m.add(0, 100 + i, i, keyNames[i]);
        int effective = prefs().getInt(KEY_SCALE_EFFECTIVE, 0);
        int forced = prefs().getInt(KEY_SCALE, 0);
        String now = forced > 0 ? forced + "%" : "auto" + (effective > 0 ? " = " + effective + "%" : "");
        m.add(1, 200, 50, "Escala da UI + (" + now + ")");
        m.add(1, 201, 51, "Escala da UI −");
        m.add(1, 202, 52, "Escala da UI: automatica");
        m.add(2, 300, 60, "Botao de atalhos: posicao padrao");
        m.add(3, 400, 70, "Gerar APK do jogo...");
        pm.setOnMenuItemClickListener(new PopupMenu.OnMenuItemClickListener() {
            public boolean onMenuItemClick(MenuItem item) {
                int id = item.getItemId();
                if (id >= 100 && id < 100 + keys.length) {
                    pressKey(keys[id - 100][0], keyNames[id - 100]);
                } else if (id == 200 || id == 201) {
                    changeScale(id == 200 ? 5 : -5);
                } else if (id == 202) {
                    setScale(0);
                } else if (id == 400) {
                    closePanel("apk");
                    sendCommand("apk");
                } else if (id == 300) {
                    sFx = 1f;
                    sFy = 0.5f;
                    prefs().edit().putFloat(KEY_FLOAT_X, sFx).putFloat(KEY_FLOAT_Y, sFy).apply();
                    Log.i(TAG, "botao de volta a posicao padrao");
                    placeFab();
                    if (sPanelOpen) placePanel();
                }
                return true;
            }
        });
        pm.show();
    }

    // ------------------------------------------------------------ escala

    private static void changeScale(int delta) {
        SharedPreferences p = prefs();
        int base = p.getInt(KEY_SCALE, 0);
        if (base <= 0) base = p.getInt(KEY_SCALE_EFFECTIVE, 0);
        if (base <= 0) base = 50;
        setScale(Math.max(25, Math.min(400, base + delta)));
    }

    private static void setScale(int pct) {
        prefs().edit().putInt(KEY_SCALE, pct).apply();
        Log.i(TAG, "ui_scale_percent -> " + pct + (pct == 0 ? " (automatico)" : "%"));
        Toast.makeText(sActivity, pct == 0 ? "Escala da UI: automatica" : "Escala da UI: " + pct + "%", Toast.LENGTH_SHORT).show();
        sendCommand("scale:" + pct);
    }

    // ------------------------------------------------------------ teclas

    private static void keyDown(int keycode) {
        if (sNativeBroken) return;
        boolean prev = sSending;
        sSending = true;
        try { SDLActivity.onNativeKeyDown(keycode); }
        catch (Throwable t) { nativeBroken(t); }
        finally { sSending = prev; }
    }

    private static void keyUp(int keycode) {
        if (sNativeBroken) return;
        boolean prev = sSending;
        sSending = true;
        try { SDLActivity.onNativeKeyUp(keycode); }
        catch (Throwable t) { nativeBroken(t); }
        finally { sSending = prev; }
    }

    private static void nativeBroken(Throwable t) {
        sNativeBroken = true;
        Log.e(TAG, "SDLActivity.onNativeKey* indisponivel; barra sem efeito", t);
    }

    private static void pressKey(int keycode, String name) {
        Log.i(TAG, "tecla " + name + modsDesc());
        keyDown(keycode);
        keyUp(keycode);
        releaseOneShots(name);
    }

    // Combinacao com Ctrl/Shift: se o modificador ja estiver preso pela barra, so
    // manda a tecla; senao aperta e solta em volta dela.
    private static void combo(boolean ctrl, boolean shift, int keycode, String name) {
        boolean needCtrl = ctrl && sMod[MOD_CTRL] == OFF;
        boolean needShift = shift && sMod[MOD_SHIFT] == OFF;
        Log.i(TAG, "atalho " + name + " (" + (ctrl ? "Ctrl+" : "") + (shift ? "Shift+" : "") + label(keycode) + ")" + modsDesc());
        if (needCtrl) keyDown(KeyEvent.KEYCODE_CTRL_LEFT);
        if (needShift) keyDown(KeyEvent.KEYCODE_SHIFT_LEFT);
        keyDown(keycode);
        keyUp(keycode);
        if (needShift) keyUp(KeyEvent.KEYCODE_SHIFT_LEFT);
        if (needCtrl) keyUp(KeyEvent.KEYCODE_CTRL_LEFT);
        releaseOneShots(name);
    }

    private static String label(int keycode) {
        String s = KeyEvent.keyCodeToString(keycode);
        return s.startsWith("KEYCODE_") ? s.substring(8) : s;
    }

    private static String modsDesc() {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < 3; i++)
            if (sMod[i] != OFF) sb.append(sb.length() == 0 ? " com " : "+").append(MOD_NAMES[i]).append(sMod[i] == LOCKED ? "(travado)" : "");
        return sb.toString();
    }

    private static void tapModifier(int m) {
        long now = SystemClock.uptimeMillis();
        int before = sMod[m];
        if (before == ONESHOT && now - sModTap[m] <= DOUBLE_TAP_MS) {
            sMod[m] = LOCKED;                 // ja esta pressionado
        } else if (before == OFF) {
            sMod[m] = ONESHOT;
            keyDown(MOD_KEYCODES[m]);
        } else {
            sMod[m] = OFF;
            keyUp(MOD_KEYCODES[m]);
        }
        sModTap[m] = now;
        Log.i(TAG, MOD_NAMES[m] + ": " + stateName(before) + " -> " + stateName(sMod[m]));
        updateModButtons();
        sendMods();
    }

    private static String stateName(int s) { return s == OFF ? "solto" : s == ONESHOT ? "proxima tecla" : "travado"; }

    private static void releaseOneShots(String why) {
        boolean changed = false;
        for (int i = 0; i < 3; i++) {
            if (sMod[i] == ONESHOT) {
                sMod[i] = OFF;
                keyUp(MOD_KEYCODES[i]);
                changed = true;
            }
        }
        if (changed) {
            Log.i(TAG, "modificadores de um uso soltos (" + why + ")");
            updateModButtons();
            sendMods();
        }
    }

    private static void sendMods() {
        sendCommand("mods:" + sMod[MOD_CTRL] + sMod[MOD_SHIFT] + sMod[MOD_ALT]);
    }

    private static void updateModButtons() {
        for (int i = 0; i < 3; i++) {
            TextView b = sModButtons[i];
            if (b == null) continue;
            setColor(b, sMod[i] == OFF ? COLOR_BUTTON : sMod[i] == ONESHOT ? COLOR_ONESHOT : COLOR_LOCKED);
            b.setPaintFlags(sMod[i] == LOCKED ? (b.getPaintFlags() | android.graphics.Paint.UNDERLINE_TEXT_FLAG)
                                              : (b.getPaintFlags() & ~android.graphics.Paint.UNDERLINE_TEXT_FLAG));
        }
        updateBadge();
    }

    private static void tapRightClick() {
        long now = SystemClock.uptimeMillis();
        int before = sRightClick;
        if (before == ONESHOT && now - sRightTap <= DOUBLE_TAP_MS) sRightClick = LOCKED;
        else if (before == OFF) sRightClick = ONESHOT;
        else sRightClick = OFF;
        sRightTap = now;
        Log.i(TAG, "clique direito no proximo toque: " + stateName(before) + " -> " + stateName(sRightClick));
        updateRightButton();
        sendCommand("rclick:" + sRightClick);
    }

    private static boolean sPanMode;
    private static TextView sPanButton;

    private static void updatePanButton() {
        if (sPanButton == null) return;
        setColor(sPanButton, sPanMode ? COLOR_LOCKED : COLOR_BUTTON);
        updateBadge();
    }

    private static void updateRightButton() {
        updateBadge();
        if (sRightButton == null) return;
        setColor(sRightButton, sRightClick == OFF ? COLOR_BUTTON : sRightClick == ONESHOT ? COLOR_ONESHOT : COLOR_LOCKED);
    }

    // ------------------------------------------------------------ canal com o C#

    private static void sendCommand(String cmd) {
        synchronized (sCommands) {
            while (sCommands.size() > 32) sCommands.pollFirst(); // C# ainda nao instalado
            sCommands.addLast(cmd);
        }
        keyDown(KeyEvent.KEYCODE_HELP);
        keyUp(KeyEvent.KEYCODE_HELP);
    }

    private static void returnToIde(String action) {
        closePanel(action);
        if ("inspect".equals(action) && sActivity.getIntent().getBooleanExtra("gmide.debug", false)) {
            try {
                sActivity.getClass().getMethod("gmideEnterDebugPip").invoke(sActivity);
            } catch (Exception e) { Log.e(TAG, "Falha ao abrir debug PiP", e); }
            return;
        }
        android.content.Intent intent = new android.content.Intent();
        intent.setClassName(sActivity.getPackageName(), "io.gmide.GmIdeActivity");
        intent.addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK | android.content.Intent.FLAG_ACTIVITY_SINGLE_TOP);
        intent.putExtra("gmide.preview_action", action);
        sActivity.startActivity(intent);
        // Remove the preview task immediately; onDestroy kills only :game. The IDE also
        // receives stop_program so its build/debugger state completes normally.
        if ("stop".equals(action)) sActivity.finishAndRemoveTask();
    }

    public static void setPreviewPip(boolean pip) {
        if (!sPreview) return;
        closePanel("pip");
        if (sFab != null) sFab.setVisibility(pip ? View.GONE : View.VISIBLE);
    }

    /**
     * Enfileira um comando para o C# de qualquer thread/activity (ex.: ApkExportActivity ao
     * confirmar, ApkExport.cs para voltar o alvo). Vai pela thread de UI; se a IDE ainda nao
     * voltou ao primeiro plano, o HELP so e processado quando o SDL retomar.
     */
    public static void postCommand(final String cmd) {
        Handler h = sMain != null ? sMain : new Handler(android.os.Looper.getMainLooper());
        // atraso: a GmIdeActivity (SDL) precisa ter voltado ao primeiro plano
        h.postDelayed(new Runnable() {
            public void run() { sendCommand(cmd); }
        }, 400);
    }

    /** Chamado pelo C# (TouchInput) ao ver SDL_SCANCODE_HELP. null = fila vazia. Qualquer thread. */
    public static String takeCommand() {
        synchronized (sCommands) {
            return sCommands.pollFirst();
        }
    }

    /** Chamado pelo C#: uma tecla (nao modificadora) ou um clique de toque terminou. Qualquer thread. */
    public static void onKeyUsed(final String what) {
        if (sSending || sMain == null) return; // a barra cuida das teclas que ela mesma manda
        sMain.post(new Runnable() {
            public void run() { releaseOneShots(what); }
        });
    }

    /** Chamado pelo C#: o SDL soltou um modificador que nao foi a barra (teclado virtual, foco perdido). */
    public static void onModifierKeyUp(final int scancode) {
        if (sSending || sMain == null) return;
        sMain.post(new Runnable() {
            public void run() {
                int m = scancode == 224 || scancode == 228 ? MOD_CTRL
                      : scancode == 225 || scancode == 229 ? MOD_SHIFT
                      : scancode == 226 || scancode == 230 ? MOD_ALT : -1;
                if (m < 0 || sMod[m] == OFF) return;
                Log.i(TAG, MOD_NAMES[m] + " solto pelo SDL (scancode " + scancode + "); botao volta a solto");
                sMod[m] = OFF;
                updateModButtons();
                sendMods();
            }
        });
    }

    /** Chamado pelo C#: o modo "proximo toque = clique direito" foi usado. */
    public static void onRightClickUsed() {
        if (sMain == null) return;
        sMain.post(new Runnable() {
            public void run() {
                if (sRightClick != ONESHOT) return;
                sRightClick = OFF;
                updateRightButton();
                sendCommand("rclick:0");
            }
        });
    }
}
