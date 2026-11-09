package io.gmide;

import android.app.Activity;
import android.content.res.ColorStateList;
import android.graphics.Typeface;
import android.graphics.drawable.Drawable;
import android.graphics.drawable.GradientDrawable;
import android.graphics.drawable.RippleDrawable;
import android.os.Handler;
import android.os.Looper;
import android.text.TextUtils;
import android.util.Log;
import android.util.TypedValue;
import android.view.Gravity;
import android.view.HapticFeedbackConstants;
import android.view.View;
import android.view.ViewGroup;
import android.widget.FrameLayout;
import android.widget.ImageView;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import org.json.JSONArray;
import org.json.JSONObject;

import java.util.ArrayList;

// Casca mobile-first da IDE (docs/mobile-ui.md, secao "Casca"). Lado C#: host/MobileShell.cs.
//
// No lugar da moldura de desktop (menu, barra de ferramentas, faixas de abas, docks fixos):
//  - barra superior (48dp): projeto e editor atual (toque = seletor de editores), progresso do build,
//    Executar, Depurar, Parar e o menu ⋮ com TODOS os itens do menu original e da barra de ferramentas;
//  - trilho a esquerda (64dp): um botao por painel (abas dos docks: Recursos, Inspetor, Saida...).
//    Toque abre o painel como gaveta; toque de novo, outro painel ou toque fora fecha;
//  - folhas laterais com linhas de 48dp: menu (navegacao por niveis, com voltar) e editores abertos.
// A superficie do SDL encolhe para a area livre (padding do mLayout, GmIdeActivity.applyBottomPadding):
// nada da IDE fica escondido atras da barra. Com o teclado aberto no editor de codigo, barra e trilho
// saem (o codigo precisa da altura toda).
//
// Canal: comandos "shell:..." pela fila da barra de atalhos (ShortcutBar.command -> tecla HELP -> C#).
// O C# responde com onState (estado, so quando muda) e onMenu (um nivel do menu). Nenhuma view aqui
// pega foco: o teclado continua indo para a IDE.
public final class MobileShell {
    private static final String TAG = "GmIde.Shell";

    static final int BAR_DP = 48, RAIL_DP = 64, ROW_DP = 52;
    private static final int C_BAR = 0xFF1A1B1F, C_RAIL = 0xFF17181B, C_LINE = 0xFF2C2E34;
    private static final int C_SHEET = 0xFF1F2025, C_TEXT = 0xFFE8E8E8, C_DIM = 0xFF9A9CA3;
    private static final int C_ACCENT = 0xFF3DDC97, C_ACCENT_BG = 0xFF233A2F, C_RUN = 0xFF1F8F5A;
    private static final int C_STOP = 0xFFE5534B, C_PRESS = 0x33FFFFFF;

    private static Activity sAct;
    private static Handler sMain;
    private static FrameLayout sContent;
    private static float sD = 1f;

    private static LinearLayout sBar, sRail;
    private static ScrollView sRailScroll;
    private static TextView sTitle, sProject, sProgress, sRunLabel;
    private static View sRun, sDebug, sStop, sGameOptions;
    private static boolean sOn, sVisible;
    private static JSONObject sState = new JSONObject();
    private static String sRailKey = "";

    // folha aberta (menu ou editores)
    private static FrameLayout sOverlay;
    private static LinearLayout sSheetBody;
    private static TextView sSheetTitle;
    private static ImageView sSheetBack;
    private static int sSheetKind;               // 0 nenhuma, 1 menu, 2 editores
    private static final ArrayList<String> sMenuPath = new ArrayList<String>();
    private static final ArrayList<String> sMenuTitles = new ArrayList<String>();

    private MobileShell() { }

    // ------------------------------------------------------------ instalacao

    public static void install(Activity a) {
        try {
            sAct = a;
            sMain = new Handler(Looper.getMainLooper());
            sD = a.getResources().getDisplayMetrics().density;
            sContent = (FrameLayout) a.findViewById(android.R.id.content);
            buildBar();
            buildRail();
            applyVisibility();
            Log.i(TAG, "casca instalada (aguardando o C#)");
        } catch (Throwable t) {
            Log.e(TAG, "install falhou", t);
        }
    }

    private static int dp(float v) { return Math.round(v * sD); }

    /** Faixa reservada no topo (px), lida por GmIdeActivity para o padding da superficie. */
    public static int insetTop() { return sVisible ? dp(BAR_DP) : 0; }

    /** Faixa reservada a esquerda (px). */
    public static int insetLeft() { return sVisible ? dp(RAIL_DP) : 0; }

    /** GmIdeActivity: teclado/insets mudaram. Thread de UI. */
    public static void onLayoutChanged() { applyVisibility(); }

    /** Voltar do Android tem algo a fechar na casca (folha aberta ou gaveta)? */
    public static boolean wantsBack() {
        return sSheetKind != 0 || (sOn && sVisible && anyPanelOpen());
    }

    /** Botao voltar do Android: fecha a folha aberta ou a gaveta. */
    public static boolean handleBack() {
        if (sSheetKind == 1 && sMenuPath.size() > 1) { menuBack(); return true; }
        if (sSheetKind != 0) { closeSheet(); return true; }
        if (sOn && anyPanelOpen()) { send("closepanels"); return true; }   // voltar fecha a gaveta
        return false;
    }

    private static boolean anyPanelOpen() {
        JSONArray p = sState.optJSONArray("panels");
        for (int i = 0; p != null && i < p.length(); i++) if (p.optJSONObject(i).optInt("o") == 1) return true;
        return false;
    }

    private static void applyVisibility() {
        if (sBar == null) return;
        boolean vis = sOn && !GmIdeActivity.isCodeKeyboard();
        if (!vis && sSheetKind != 0) closeSheet();
        int v = vis ? View.VISIBLE : View.GONE;
        sBar.setVisibility(v);
        sRailScroll.setVisibility(v);
        if (vis != sVisible) {
            sVisible = vis;
            GmIdeActivity.relayout();
            Log.i(TAG, vis ? "casca visivel" : "casca oculta");
        }
    }

    private static void send(String cmd) { ShortcutBar.command("shell:" + cmd); }

    // ------------------------------------------------------------ barra superior

    private static void buildBar() {
        sBar = new LinearLayout(sAct);
        sBar.setOrientation(LinearLayout.HORIZONTAL);
        sBar.setGravity(Gravity.CENTER_VERTICAL);
        sBar.setPadding(dp(4), 0, dp(4), 0);
        sBar.setClickable(true);   // toques na barra nao vazam para a IDE

        // Projeto / editor atual: abre o seletor de editores (substitui as faixas de abas).
        LinearLayout chip = new LinearLayout(sAct);
        chip.setOrientation(LinearLayout.HORIZONTAL);
        chip.setGravity(Gravity.CENTER_VERTICAL);
        chip.setPadding(dp(10), 0, dp(6), 0);
        chip.setBackground(ripple(0, dp(8)));
        chip.addView(icon(ShellIcon.TABS, C_DIM, 22), new LinearLayout.LayoutParams(dp(22), dp(22)));
        LinearLayout texts = new LinearLayout(sAct);
        texts.setOrientation(LinearLayout.VERTICAL);
        texts.setPadding(dp(10), 0, dp(4), 0);
        sProject = text("", 11, C_DIM, false);
        sTitle = text("GameMaker", 15, C_TEXT, true);
        texts.addView(sProject);
        texts.addView(sTitle);
        sProject.setMaxWidth(dp(300));
        sTitle.setMaxWidth(dp(300));
        chip.addView(texts, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        chip.addView(icon(ShellIcon.CHEVRON_DOWN, C_DIM, 20), new LinearLayout.LayoutParams(dp(20), dp(20)));
        chip.setOnClickListener(new View.OnClickListener() {
            @Override public void onClick(View v) { tap(v); openEditors(); }
        });
        chip.setContentDescription("Trocar editor");
        sBar.addView(chip, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(44)));
        sBar.addView(new View(sAct), new LinearLayout.LayoutParams(0, 1, 1f));   // espaco livre

        sProgress = text("", 12, C_DIM, false);
        sProgress.setMaxWidth(dp(260));
        sProgress.setPadding(dp(8), 0, dp(8), 0);
        sProgress.setVisibility(View.GONE);
        sBar.addView(sProgress);

        // Acoes frequentes sempre a vista, com icone e nome (no teste, achar o Game Options no menu foi dificil).
        sGameOptions = chipButton(ShellIcon.GEAR, "Game Options", new View.OnClickListener() {
            @Override public void onClick(View v) { tap(v); send("tool:ide_toolbar_gamesettings"); }
        });
        // Exportar: Android APK, Web (HTML5) e Windows (EXE), a mesma tela do "Exportar" da barra de atalhos.
        chipButton(ShellIcon.EXPORT, "Exportar", new View.OnClickListener() {
            @Override public void onClick(View v) { tap(v); ShortcutBar.command("apk"); }
        });

        // Executar: botao principal, com rotulo.
        LinearLayout run = new LinearLayout(sAct);
        run.setOrientation(LinearLayout.HORIZONTAL);
        run.setGravity(Gravity.CENTER_VERTICAL);
        run.setPadding(dp(12), 0, dp(16), 0);
        run.setBackground(ripple(C_RUN, dp(18)));
        run.addView(icon(ShellIcon.PLAY, 0xFFFFFFFF, 20), new LinearLayout.LayoutParams(dp(20), dp(20)));
        sRunLabel = text("Executar", 14, 0xFFFFFFFF, true);
        sRunLabel.setPadding(dp(6), 0, 0, 0);
        run.addView(sRunLabel);
        run.setContentDescription("Executar (F5)");
        run.setOnClickListener(new View.OnClickListener() {
            @Override public void onClick(View v) { tap(v); send("run"); }
        });
        LinearLayout.LayoutParams rlp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(38));
        rlp.setMargins(dp(4), 0, dp(4), 0);
        sBar.addView(run, rlp);
        sRun = run;

        sDebug = iconButton(ShellIcon.BUG, C_TEXT, "Depurar (F6)", new View.OnClickListener() {
            @Override public void onClick(View v) { tap(v); send("debug"); }
        });
        sBar.addView(sDebug);
        sStop = iconButton(ShellIcon.STOP, C_TEXT, "Parar", new View.OnClickListener() {
            @Override public void onClick(View v) { tap(v); send("stop"); }
        });
        sBar.addView(sStop);
        View more = iconButton(ShellIcon.MORE, C_TEXT, "Menu", new View.OnClickListener() {
            @Override public void onClick(View v) { tap(v); openMenu(); }
        });
        sBar.addView(more);

        FrameLayout.LayoutParams flp = new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(BAR_DP), Gravity.TOP);
        sContent.addView(sBar, flp);
        sBar.setBackground(layered(C_BAR, C_LINE));   // fundo com a linha divisoria embaixo
    }

    private static View chipButton(int iconType, String label, View.OnClickListener click) {
        LinearLayout b = new LinearLayout(sAct);
        b.setOrientation(LinearLayout.HORIZONTAL);
        b.setGravity(Gravity.CENTER_VERTICAL);
        b.setPadding(dp(10), 0, dp(12), 0);
        b.setBackground(ripple(0xFF2A2C33, dp(18)));
        b.addView(icon(iconType, C_TEXT, 20), new LinearLayout.LayoutParams(dp(20), dp(20)));
        TextView t = text(label, 13, C_TEXT, false);
        t.setPadding(dp(6), 0, 0, 0);
        b.addView(t);
        b.setContentDescription(label);
        b.setOnClickListener(click);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT, dp(38));
        lp.setMargins(dp(4), 0, dp(2), 0);
        sBar.addView(b, lp);
        return b;
    }

    private static Drawable layered(int fill, int bottomLine) {
        GradientDrawable base = new GradientDrawable();
        base.setColor(bottomLine);
        GradientDrawable top = new GradientDrawable();
        top.setColor(fill);
        android.graphics.drawable.LayerDrawable ld = new android.graphics.drawable.LayerDrawable(new Drawable[] { base, top });
        ld.setLayerInset(1, 0, 0, 0, Math.max(1, dp(1)));
        return ld;
    }

    // ------------------------------------------------------------ trilho de paineis

    private static void buildRail() {
        sRailScroll = new ScrollView(sAct);
        sRailScroll.setVerticalScrollBarEnabled(false);
        sRailScroll.setFillViewport(true);
        GradientDrawable bg = new GradientDrawable();
        bg.setColor(C_RAIL);
        sRailScroll.setBackground(bg);
        sRailScroll.setClickable(true);
        sRail = new LinearLayout(sAct);
        sRail.setOrientation(LinearLayout.VERTICAL);
        sRail.setPadding(0, dp(6), 0, dp(6));
        sRailScroll.addView(sRail, new ScrollView.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
        FrameLayout.LayoutParams flp = new FrameLayout.LayoutParams(dp(RAIL_DP), ViewGroup.LayoutParams.MATCH_PARENT, Gravity.LEFT);
        flp.topMargin = dp(BAR_DP);
        sContent.addView(sRailScroll, flp);
    }

    private static void renderRail(JSONArray panels) {
        StringBuilder key = new StringBuilder();
        for (int i = 0; panels != null && i < panels.length(); i++) {
            JSONObject p = panels.optJSONObject(i);
            key.append(p.optInt("d")).append('/').append(p.optInt("i")).append('/').append(p.optString("t"))
               .append('/').append(p.optInt("o")).append(';');
        }
        if (key.toString().equals(sRailKey)) return;
        sRailKey = key.toString();
        sRail.removeAllViews();
        // Paineis laterais (Recursos, Inspetor) primeiro; os do dock de baixo (Saida, Erros...) depois do divisor.
        ArrayList<JSONObject> order = new ArrayList<JSONObject>();
        for (int pass = 0; pass < 2; pass++)
            for (int i = 0; panels != null && i < panels.length(); i++) {
                JSONObject p = panels.optJSONObject(i);
                if ((p.optInt("d") == 2) == (pass == 1)) order.add(p);
            }
        boolean divided = false;
        for (int i = 0; i < order.size(); i++) {
            final JSONObject p = order.get(i);
            if (p.optInt("d") == 2 && i > 0 && !divided) {
                divided = true;
                View div = new View(sAct);
                div.setBackgroundColor(C_LINE);
                LinearLayout.LayoutParams dlp = new LinearLayout.LayoutParams(dp(32), Math.max(1, dp(1)));
                dlp.gravity = Gravity.CENTER_HORIZONTAL;
                dlp.setMargins(0, dp(6), 0, dp(6));
                sRail.addView(div, dlp);
            }
            final boolean open = p.optInt("o") == 1;
            String label = panelLabel(p);
            LinearLayout item = new LinearLayout(sAct);
            item.setOrientation(LinearLayout.VERTICAL);
            item.setGravity(Gravity.CENTER_HORIZONTAL);
            item.setPadding(dp(2), dp(4), dp(2), dp(5));
            item.setBackground(ripple(0, 0));
            FrameLayout pill = new FrameLayout(sAct);
            GradientDrawable pbg = new GradientDrawable();
            pbg.setCornerRadius(dp(14));
            pbg.setColor(open ? C_ACCENT_BG : 0);
            pill.setBackground(pbg);
            ImageView ic = icon(panelIcon(p), open ? C_ACCENT : C_TEXT, 24);
            pill.addView(ic, new FrameLayout.LayoutParams(dp(24), dp(24), Gravity.CENTER));
            item.addView(pill, new LinearLayout.LayoutParams(dp(52), dp(30)));
            TextView t = text(label, 10, open ? C_ACCENT : C_DIM, open);
            t.setGravity(Gravity.CENTER);
            t.setPadding(0, dp(3), 0, 0);
            item.addView(t, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
            item.setContentDescription(p.optString("t"));
            item.setOnClickListener(new View.OnClickListener() {
                @Override public void onClick(View v) { tap(v); send("panel:" + p.optInt("d") + ":" + p.optInt("i")); }
            });
            sRail.addView(item, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));
            if (open) {
                // painel aberto sempre a vista no trilho (o destaque mostra qual gaveta esta aberta)
                final View target = item;
                sRailScroll.post(new Runnable() {
                    @Override public void run() {
                        int top = target.getTop(), bottom = target.getBottom();
                        int y = sRailScroll.getScrollY(), h = sRailScroll.getHeight();
                        if (top < y) sRailScroll.smoothScrollTo(0, top);
                        else if (bottom > y + h) sRailScroll.smoothScrollTo(0, bottom - h);
                    }
                });
            }
        }
    }

    // Paineis conhecidos: icone proprio e rotulo curto em portugues (cabe no trilho de 64dp). O nome
    // original continua na descricao de acessibilidade e no cabecalho do proprio painel.
    private static final String[][] PANELS = {
        // palavra-chave (titulo ou LayoutID, minusculas), rotulo curto, icone
        { "asset", "Recursos", "" + ShellIcon.FOLDER },
        { "inspect", "Inspetor", "" + ShellIcon.INSPECTOR },
        { "output", "Saída", "" + ShellIcon.OUTPUT },
        { "compile error", "Erros", "" + ShellIcon.WARNING },
        { "error", "Erros", "" + ShellIcon.WARNING },
        { "search", "Busca", "" + ShellIcon.SEARCH },
        { "source control", "Versões", "" + ShellIcon.BRANCH },
        { "breakpoint", "Pausas", "" + ShellIcon.BREAKPOINT },
        { "prefab", "Prefabs", "" + ShellIcon.CUBE },
        { "layer", "Camadas", "" + ShellIcon.LAYERS },
        { "debug", "Depurar", "" + ShellIcon.BUG },
    };

    private static String[] panelInfo(JSONObject p) {
        String s = (p.optString("t") + " " + p.optString("id")).toLowerCase();
        if (p.optInt("insp") == 1 && !s.contains("asset")) return PANELS[1];
        for (String[] k : PANELS) if (s.contains(k[0])) return k;
        return null;
    }

    private static String panelLabel(JSONObject p) {
        String[] k = panelInfo(p);
        return k != null ? k[1] : p.optString("t");
    }

    private static int panelIcon(JSONObject p) {
        String[] k = panelInfo(p);
        return k != null ? Integer.parseInt(k[2]) : ShellIcon.PANEL;
    }

    // ------------------------------------------------------------ estado vindo do C#

    /** C# (thread da IDE): estado da casca em JSON, so quando muda. */
    public static void onState(final String json) {
        if (sMain == null) return;
        sMain.post(new Runnable() {
            @Override public void run() { applyState(json); }
        });
    }

    private static void applyState(String json) {
        if (sBar == null) return;
        try {
            JSONObject s = new JSONObject(json);
            sState = s;
            boolean on = s.optInt("on") == 1;
            if (on != sOn) { sOn = on; Log.i(TAG, on ? "C#: casca ativa" : "C#: casca inativa"); }
            applyVisibility();
            if (!on) return;
            String proj = s.optString("proj");
            String ed = s.optString("ed");
            sProject.setText(proj.isEmpty() ? "Projeto" : proj);
            sTitle.setText(ed.isEmpty() ? "Editores" : ed);
            String prog = s.optString("prog");
            sProgress.setText(prog);
            sProgress.setVisibility(prog.isEmpty() ? View.GONE : View.VISIBLE);
            enable(sRun, s.optInt("play") == 1);
            enable(sDebug, s.optInt("debug") == 1);
            enable(sStop, s.optInt("stop") == 1);
            enable(sGameOptions, s.optInt("go", 1) == 1);
            renderRail(s.optJSONArray("panels"));
            if (sSheetKind == 2) renderEditors();
        } catch (Exception e) {
            Log.w(TAG, "estado invalido: " + e.getMessage());
        }
    }

    private static void enable(View v, boolean on) {
        v.setEnabled(on);
        v.setAlpha(on ? 1f : 0.35f);
    }

    // ------------------------------------------------------------ folhas (menu, editores)

    private static void openSheet(int kind, boolean right) {
        closeSheet();
        sSheetKind = kind;
        sOverlay = new FrameLayout(sAct);
        sOverlay.setClickable(true);
        View scrim = new View(sAct);
        scrim.setBackgroundColor(0x99000000);
        scrim.setOnClickListener(new View.OnClickListener() {
            @Override public void onClick(View v) { closeSheet(); }
        });
        sOverlay.addView(scrim, new FrameLayout.LayoutParams(-1, -1));

        LinearLayout sheet = new LinearLayout(sAct);
        sheet.setOrientation(LinearLayout.VERTICAL);
        sheet.setClickable(true);
        GradientDrawable bg = new GradientDrawable();
        bg.setColor(C_SHEET);
        sheet.setBackground(bg);
        sheet.setElevation(dp(16));

        LinearLayout head = new LinearLayout(sAct);
        head.setOrientation(LinearLayout.HORIZONTAL);
        head.setGravity(Gravity.CENTER_VERTICAL);
        head.setPadding(dp(4), 0, dp(4), 0);
        sSheetBack = (ImageView) iconButton(ShellIcon.BACK, C_TEXT, "Voltar", new View.OnClickListener() {
            @Override public void onClick(View v) { tap(v); menuBack(); }
        });
        sSheetBack.setVisibility(View.GONE);
        head.addView(sSheetBack);
        sSheetTitle = text("", 17, C_TEXT, true);
        sSheetTitle.setPadding(dp(12), 0, dp(8), 0);
        head.addView(sSheetTitle, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
        head.addView(iconButton(ShellIcon.CLOSE, C_TEXT, "Fechar", new View.OnClickListener() {
            @Override public void onClick(View v) { tap(v); closeSheet(); }
        }));
        sheet.addView(head, new LinearLayout.LayoutParams(-1, dp(56)));
        View line = new View(sAct);
        line.setBackgroundColor(C_LINE);
        sheet.addView(line, new LinearLayout.LayoutParams(-1, Math.max(1, dp(1))));

        ScrollView scroll = new ScrollView(sAct);
        scroll.setVerticalScrollBarEnabled(true);
        sSheetBody = new LinearLayout(sAct);
        sSheetBody.setOrientation(LinearLayout.VERTICAL);
        sSheetBody.setPadding(0, dp(4), 0, dp(12));
        scroll.addView(sSheetBody, new ScrollView.LayoutParams(-1, -2));
        sheet.addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1f));

        int w = sContent.getWidth() > 0 ? sContent.getWidth() : sAct.getResources().getDisplayMetrics().widthPixels;
        int sw = Math.min(dp(400), Math.round(w * 0.6f));
        sOverlay.addView(sheet, new FrameLayout.LayoutParams(sw, -1, right ? Gravity.RIGHT : Gravity.LEFT));
        sContent.addView(sOverlay, new FrameLayout.LayoutParams(-1, -1));
        sheet.setTranslationX(right ? sw : -sw);
        sheet.animate().translationX(0).setDuration(160).start();
    }

    private static void closeSheet() {
        if (sOverlay != null) {
            sContent.removeView(sOverlay);
            sOverlay = null;
        }
        sSheetKind = 0;
        sMenuPath.clear();
        sMenuTitles.clear();
    }

    private static void loading() {
        sSheetBody.removeAllViews();
        TextView t = text("Carregando…", 14, C_DIM, false);
        t.setPadding(dp(20), dp(16), dp(20), dp(16));
        sSheetBody.addView(t);
    }

    // ---- menu ⋮ (menu original + barra de ferramentas, nivel a nivel)

    private static void openMenu() {
        openSheet(1, true);
        sMenuPath.add("");
        sMenuTitles.add("Menu");
        requestMenu();
    }

    private static void requestMenu() {
        String path = sMenuPath.get(sMenuPath.size() - 1);
        sSheetTitle.setText(sMenuTitles.get(sMenuTitles.size() - 1));
        sSheetBack.setVisibility(sMenuPath.size() > 1 ? View.VISIBLE : View.GONE);
        loading();
        send("menu:" + path);
    }

    private static void menuBack() {
        if (sSheetKind != 1 || sMenuPath.size() <= 1) return;
        sMenuPath.remove(sMenuPath.size() - 1);
        sMenuTitles.remove(sMenuTitles.size() - 1);
        requestMenu();
    }

    /** C# (thread da IDE): itens de um nivel do menu. */
    public static void onMenu(final String path, final String json) {
        if (sMain == null) return;
        sMain.post(new Runnable() {
            @Override public void run() { renderMenu(path, json); }
        });
    }

    private static void renderMenu(String path, String json) {
        if (sSheetKind != 1 || sMenuPath.isEmpty() || !path.equals(sMenuPath.get(sMenuPath.size() - 1))) return;
        sSheetBody.removeAllViews();
        try {
            JSONArray items = new JSONArray(json);
            for (int i = 0; i < items.length(); i++) {
                JSONObject it = items.getJSONObject(i);
                String k = it.optString("k");
                if ("sep".equals(k)) { addDivider(); continue; }
                if ("hdr".equals(k)) {
                    addHeader(it.optString("t"));
                    if (path.isEmpty() && i == 0) {
                        // Exportar e do host (ApkExportActivity), nao da moldura da IDE: entra aqui a mao.
                        addRow("Exportar jogo (APK, Web, Windows)", null, ShellIcon.EXPORT, false, true, false, new View.OnClickListener() {
                            @Override public void onClick(View v) { tap(v); closeSheet(); ShortcutBar.command("apk"); }
                        }, null);
                    }
                    continue;
                }
                final String child = path.isEmpty() ? String.valueOf(i) : path + "." + i;
                final String label = it.optString("t");
                final boolean sub = "sub".equals(k);
                boolean enabled = it.optInt("e") == 1;
                int lead = "check".equals(k) ? (it.optInt("c") == 1 ? ShellIcon.CHECK : -2) : -1;
                int tool = toolIcon(it.optString("ic"));
                if (tool >= 0) lead = tool;
                addRow(label, sub ? null : it.optString("h"), lead, sub, enabled, false, new View.OnClickListener() {
                    @Override public void onClick(View v) {
                        tap(v);
                        if (sub) {
                            sMenuPath.add(child);
                            sMenuTitles.add(label);
                            requestMenu();
                        } else {
                            send("click:" + child);
                            closeSheet();
                        }
                    }
                }, null);
            }
            if (items.length() == 0) {
                TextView t = text("Nada aqui agora", 14, C_DIM, false);
                t.setPadding(dp(20), dp(16), dp(20), dp(16));
                sSheetBody.addView(t);
            }
            if (path.isEmpty()) {
                addDivider();
                addRow("Interface clássica (desktop)", null, ShellIcon.WINDOW, false, true, false, new View.OnClickListener() {
                    @Override public void onClick(View v) {
                        tap(v);
                        closeSheet();
                        send("mode:0");
                    }
                }, null);
            }
        } catch (Exception e) {
            Log.w(TAG, "menu invalido: " + e.getMessage());
        }
    }

    // Icone de cada botao da moldura pelo LayoutID (MobileShell.cs: Node.Icon).
    private static int toolIcon(String id) {
        if (id == null || id.isEmpty()) return -1;
        if (id.equals("ide_toolbar_gamesettings")) return ShellIcon.GEAR;
        if (id.equals("target_select_button")) return ShellIcon.TARGET;
        if (id.equals("ide_toolbar_save")) return ShellIcon.SAVE;
        if (id.equals("ide_toolbar_clean")) return ShellIcon.BROOM;
        if (id.startsWith("ide_toolbar_package")) return ShellIcon.CUBE;   // o EXPORT e do "Exportar" do host
        if (id.equals("ide_toolbar_home")) return ShellIcon.HOME;
        if (id.equals("ide_toolbar_new")) return ShellIcon.FILE;
        if (id.equals("ide_toolbar_open")) return ShellIcon.FOLDER;
        if (id.equals("ide_toolbar_help")) return ShellIcon.HELP;
        if (id.equals("ide_toolbar_zoomin")) return ShellIcon.ZOOM_IN;
        if (id.equals("ide_toolbar_zoomout")) return ShellIcon.ZOOM_OUT;
        if (id.equals("ide_toolbar_zoomreset")) return ShellIcon.ZOOM_RESET;
        if (id.equals("ide_toolbar_collapsedocks")) return ShellIcon.PANEL;
        if (id.contains("account")) return ShellIcon.USER;
        return ShellIcon.TOOLS;
    }

    // ---- editores abertos (abas do workspace + janelas do workspace atual)

    private static void openEditors() {
        openSheet(2, false);
        sSheetTitle.setText("Editores abertos");
        renderEditors();
    }

    private static void renderEditors() {
        if (sSheetBody == null) return;
        sSheetBody.removeAllViews();
        JSONArray tabs = sState.optJSONArray("tabs");
        for (int i = 0; tabs != null && i < tabs.length(); i++) {
            JSONObject t = tabs.optJSONObject(i);
            final int idx = i;
            boolean active = t.optInt("a") == 1;
            View.OnClickListener close = t.optInt("c") == 1 ? new View.OnClickListener() {
                @Override public void onClick(View v) { tap(v); send("closetab:" + idx); }
            } : null;
            addRow(t.optString("t"), null, ShellIcon.TABS, false, true, active, new View.OnClickListener() {
                @Override public void onClick(View v) { tap(v); send("tab:" + idx); closeSheet(); }
            }, close);
        }
        addRow("Novo workspace", null, ShellIcon.PLUS, false, true, false, new View.OnClickListener() {
            @Override public void onClick(View v) { tap(v); send("newtab"); closeSheet(); }
        }, null);
        JSONArray wins = sState.optJSONArray("wins");
        if (wins != null && wins.length() > 0) {
            addHeader("Janelas neste workspace");
            for (int i = 0; i < wins.length(); i++) {
                JSONObject w = wins.optJSONObject(i);
                final int idx = i;
                addRow(w.optString("t"), null, ShellIcon.WINDOW, false, true, w.optInt("a") == 1, new View.OnClickListener() {
                    @Override public void onClick(View v) { tap(v); send("win:" + idx); closeSheet(); }
                }, null);
            }
        }
    }

    // ---- linhas

    private static void addHeader(String s) {
        TextView t = text(s.toUpperCase(), 11, C_DIM, true);
        t.setLetterSpacing(0.06f);
        t.setPadding(dp(20), dp(16), dp(20), dp(6));
        sSheetBody.addView(t);
    }

    private static void addDivider() {
        View v = new View(sAct);
        v.setBackgroundColor(C_LINE);
        LinearLayout.LayoutParams lp = new LinearLayout.LayoutParams(-1, Math.max(1, dp(1)));
        lp.setMargins(dp(16), dp(4), dp(16), dp(4));
        sSheetBody.addView(v, lp);
    }

    // lead: icone a esquerda (-1 nenhum, -2 espaco vazio do tamanho de um icone).
    private static void addRow(String label, String hint, int lead, boolean chevron, boolean enabled, boolean selected,
                               View.OnClickListener click, View.OnClickListener close) {
        LinearLayout row = new LinearLayout(sAct);
        row.setOrientation(LinearLayout.HORIZONTAL);
        row.setGravity(Gravity.CENTER_VERTICAL);
        row.setPadding(dp(16), 0, close != null ? 0 : dp(12), 0);
        row.setMinimumHeight(dp(ROW_DP));
        row.setBackground(ripple(selected ? C_ACCENT_BG : 0, 0));
        if (lead != -1) {
            View ic = lead >= 0 ? icon(lead, selected ? C_ACCENT : C_TEXT, 22) : new View(sAct);
            LinearLayout.LayoutParams ilp = new LinearLayout.LayoutParams(dp(22), dp(22));
            ilp.setMargins(0, 0, dp(14), 0);
            row.addView(ic, ilp);
        }
        TextView t = text(label, 15, selected ? C_ACCENT : C_TEXT, selected);
        t.setSingleLine(false);
        t.setMaxLines(2);
        row.addView(t, new LinearLayout.LayoutParams(0, ViewGroup.LayoutParams.WRAP_CONTENT, 1f));
        if (hint != null && !hint.isEmpty()) {
            TextView h = text(hint, 12, C_DIM, false);
            h.setPadding(dp(8), 0, 0, 0);
            row.addView(h);
        }
        if (chevron) row.addView(icon(ShellIcon.CHEVRON_RIGHT, C_DIM, 20), new LinearLayout.LayoutParams(dp(20), dp(20)));
        if (close != null) row.addView(iconButton(ShellIcon.CLOSE, C_DIM, "Fechar " + label, close));
        if (enabled) row.setOnClickListener(click);
        else row.setAlpha(0.38f);
        sSheetBody.addView(row, new LinearLayout.LayoutParams(-1, ViewGroup.LayoutParams.WRAP_CONTENT));
    }

    // ------------------------------------------------------------ utilitarios de view

    private static TextView text(String s, float sp, int color, boolean bold) {
        TextView t = new TextView(sAct);
        t.setText(s);
        t.setTextSize(TypedValue.COMPLEX_UNIT_SP, sp);
        t.setTextColor(color);
        t.setSingleLine(true);
        t.setEllipsize(TextUtils.TruncateAt.END);
        if (bold) t.setTypeface(Typeface.DEFAULT_BOLD);
        t.setFocusable(false);
        return t;
    }

    private static ImageView icon(int type, int color, int sizeDp) {
        ImageView v = new ImageView(sAct);
        v.setImageDrawable(new ShellIcon(type, color));
        v.setFocusable(false);
        return v;
    }

    private static View iconButton(int type, int color, String desc, View.OnClickListener click) {
        ImageView v = icon(type, color, 24);
        int pad = dp(12);
        v.setPadding(pad, pad, pad, pad);
        v.setBackground(ripple(0, dp(24)));
        v.setContentDescription(desc);
        v.setOnClickListener(click);
        v.setLayoutParams(new LinearLayout.LayoutParams(dp(48), dp(48)));
        return v;
    }

    private static Drawable ripple(int color, int radius) {
        GradientDrawable content = new GradientDrawable();
        content.setColor(color);
        content.setCornerRadius(radius);
        GradientDrawable mask = new GradientDrawable();
        mask.setColor(0xFFFFFFFF);
        mask.setCornerRadius(radius);
        return new RippleDrawable(ColorStateList.valueOf(C_PRESS), content, mask);
    }

    private static void tap(View v) {
        v.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP);
    }
}
