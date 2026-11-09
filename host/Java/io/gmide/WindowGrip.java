package io.gmide;

import android.app.Activity;
import android.graphics.drawable.GradientDrawable;
import android.os.Handler;
import android.os.Looper;
import android.os.SystemClock;
import android.util.Log;
import android.view.Gravity;
import android.view.MotionEvent;
import android.view.View;
import android.view.ViewGroup;
import android.widget.FrameLayout;
import android.widget.ImageView;

import org.libsdl.app.SDLActivity;

// Alca de redimensionar a janela flutuante em foco (lado C#: host/MobileWindowGrip.cs).
//
// As janelas flutuantes da IDE (Prefab Library, Preferencias...) so mudam de tamanho pela borda, com
// poucos pixels de area: com o dedo nao da. O C# publica o retangulo da janela flutuante
// redimensionavel em foco (px da superficie do SDL) e aqui uma alca de 48dp fica no canto inferior
// direito dela. Arrastar a alca acompanha o dedo na hora e manda o deslocamento ao C# ("grip:dx:dy",
// px), que muda Width/Height da janela; o retangulo novo reposiciona a alca. Vale com a casca mobile
// ligada ou nao. Nao pega foco: o teclado continua na IDE.
public final class WindowGrip {
    private static final String TAG = "GmIde.Grip";
    private static final int SIZE_DP = 48;
    private static final long SEND_MS = 40;

    private static Activity sAct;
    private static Handler sMain;
    private static ImageView sGrip;
    private static android.widget.LinearLayout sDock;   // "Levar para o workspace" (so na casca)
    private static int sRight = -1, sBottom = -1;   // canto da janela (px da superficie)
    private static boolean sResizable, sDockable;
    private static int sDockX, sDockY;              // borda direita / centro do botao do workspace
    private static float sLastX, sLastY, sPendX, sPendY;
    private static long sLastSend;
    private static boolean sDragging;

    private WindowGrip() { }

    public static void install(Activity a) {
        try {
            sAct = a;
            sMain = new Handler(Looper.getMainLooper());
            float d = a.getResources().getDisplayMetrics().density;
            sGrip = new ImageView(a);
            sGrip.setImageDrawable(new ShellIcon(ShellIcon.RESIZE, 0xFFFFFFFF));
            int pad = Math.round(11 * d);
            sGrip.setPadding(pad, pad, pad, pad);
            GradientDrawable bg = new GradientDrawable();
            bg.setShape(GradientDrawable.OVAL);
            bg.setColor(0xE01F8F5A);
            bg.setStroke(Math.max(1, Math.round(d)), 0x66FFFFFF);
            sGrip.setBackground(bg);
            sGrip.setElevation(6 * d);
            sGrip.setContentDescription("Redimensionar janela");
            sGrip.setVisibility(View.GONE);
            sGrip.setOnTouchListener(new View.OnTouchListener() {
                @Override
                public boolean onTouch(View v, MotionEvent e) { return touch(e); }
            });
            int size = Math.round(SIZE_DP * d);
            FrameLayout content = (FrameLayout) a.findViewById(android.R.id.content);
            content.addView(sGrip, new FrameLayout.LayoutParams(size, size, Gravity.TOP | Gravity.LEFT));

            // Botao ao lado dos botoes da barra de titulo: leva a janela flutuante para o workspace.
            sDock = new android.widget.LinearLayout(a);
            sDock.setOrientation(android.widget.LinearLayout.HORIZONTAL);
            sDock.setGravity(Gravity.CENTER_VERTICAL);
            sDock.setPadding(Math.round(10 * d), 0, Math.round(12 * d), 0);
            GradientDrawable dbg = new GradientDrawable();
            dbg.setCornerRadius(20 * d);
            dbg.setColor(0xF02A2C33);
            dbg.setStroke(Math.max(1, Math.round(d)), 0x66FFFFFF);
            sDock.setBackground(dbg);
            sDock.setElevation(6 * d);
            ImageView di = new ImageView(a);
            di.setImageDrawable(new ShellIcon(ShellIcon.DOCK_IN, 0xFFFFFFFF));
            sDock.addView(di, new android.widget.LinearLayout.LayoutParams(Math.round(20 * d), Math.round(20 * d)));
            android.widget.TextView dt = new android.widget.TextView(a);
            dt.setText("No workspace");
            dt.setTextColor(0xFFFFFFFF);
            dt.setTextSize(android.util.TypedValue.COMPLEX_UNIT_SP, 13);
            dt.setPadding(Math.round(6 * d), 0, 0, 0);
            sDock.addView(dt);
            sDock.setContentDescription("Levar a janela para o workspace");
            sDock.setVisibility(View.GONE);
            sDock.setOnClickListener(new View.OnClickListener() {
                @Override public void onClick(View v) {
                    v.performHapticFeedback(android.view.HapticFeedbackConstants.KEYBOARD_TAP);
                    v.setVisibility(View.GONE);
                    ShortcutBar.command("grip:ws");
                }
            });
            content.addView(sDock, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.WRAP_CONTENT,
                Math.round(40 * d), Gravity.TOP | Gravity.LEFT));
        } catch (Throwable t) {
            Log.e(TAG, "install falhou", t);
        }
    }

    /** C# (thread da IDE): "x,y,w,h" da janela em px da superficie do SDL; "" esconde a alca. */
    public static void setTarget(final String rect) {
        if (sMain == null) return;
        sMain.post(new Runnable() {
            @Override public void run() { apply(rect); }
        });
    }

    private static void apply(String rect) {
        if (sGrip == null) return;
        if (rect == null || rect.isEmpty()) {
            sRight = sBottom = -1;
            sResizable = sDockable = false;
            if (!sDragging) sGrip.setVisibility(View.GONE);
            sDock.setVisibility(View.GONE);
            return;
        }
        try {
            String[] p = rect.split(",");
            int x = Integer.parseInt(p[0]), y = Integer.parseInt(p[1]);
            int w = Integer.parseInt(p[2]), h = Integer.parseInt(p[3]);
            sRight = x + w;
            sBottom = y + h;
            sResizable = p.length < 5 || "1".equals(p[4]);
            sDockable = p.length >= 8 && "1".equals(p[5]);
            if (sDockable) { sDockX = Integer.parseInt(p[6]); sDockY = Integer.parseInt(p[7]); }
            if (!sDragging) place();
        } catch (Exception e) {
            Log.w(TAG, "retangulo invalido: " + rect);
        }
    }

    // Superficie do SDL = area interna do mLayout (padding = casca mobile / teclado de codigo).
    private static void place() {
        View root = SDLActivity.getContentView();
        if (root == null || sRight < 0) { sGrip.setVisibility(View.GONE); sDock.setVisibility(View.GONE); return; }
        placeDock(root);
        if (!sResizable) { sGrip.setVisibility(View.GONE); return; }
        int half = sGrip.getLayoutParams().width / 2;
        int cx = root.getLeft() + root.getPaddingLeft() + sRight;
        int cy = root.getTop() + root.getPaddingTop() + sBottom;
        // Dentro da superficie: com a janela encostada na borda, a alca fica inteira na tela.
        int maxX = root.getRight() - root.getPaddingRight() - half;
        int maxY = root.getBottom() - root.getPaddingBottom() - half;
        cx = Math.min(cx, maxX);
        cy = Math.min(cy, maxY);
        sGrip.setTranslationX(cx - half);
        sGrip.setTranslationY(cy - half);
        sGrip.setVisibility(View.VISIBLE);
        sGrip.bringToFront();
    }

    private static void placeDock(View root) {
        if (!sDockable) { sDock.setVisibility(View.GONE); return; }
        sDock.measure(View.MeasureSpec.UNSPECIFIED, View.MeasureSpec.UNSPECIFIED);
        int w = sDock.getMeasuredWidth(), h = sDock.getLayoutParams().height;
        float d = sAct.getResources().getDisplayMetrics().density;
        int left = root.getLeft() + root.getPaddingLeft();
        int top = root.getTop() + root.getPaddingTop();
        int x = left + sDockX - w - Math.round(6 * d);
        int y = top + sDockY - h / 2;
        sDock.setTranslationX(Math.max(left, x));
        sDock.setTranslationY(Math.max(top, y));
        sDock.setVisibility(View.VISIBLE);
        sDock.bringToFront();
    }

    private static boolean touch(MotionEvent e) {
        switch (e.getActionMasked()) {
            case MotionEvent.ACTION_DOWN:
                sDragging = true;
                sLastX = e.getRawX();
                sLastY = e.getRawY();
                sPendX = sPendY = 0;
                sGrip.performHapticFeedback(android.view.HapticFeedbackConstants.KEYBOARD_TAP);
                return true;
            case MotionEvent.ACTION_MOVE: {
                float dx = e.getRawX() - sLastX, dy = e.getRawY() - sLastY;
                sLastX = e.getRawX();
                sLastY = e.getRawY();
                sPendX += dx;
                sPendY += dy;
                sGrip.setTranslationX(sGrip.getTranslationX() + dx);   // acompanha o dedo ja
                sGrip.setTranslationY(sGrip.getTranslationY() + dy);
                long now = SystemClock.uptimeMillis();
                if (now - sLastSend >= SEND_MS) flush(now);
                return true;
            }
            case MotionEvent.ACTION_UP:
            case MotionEvent.ACTION_CANCEL:
                flush(SystemClock.uptimeMillis());
                sDragging = false;
                // a posicao final vem do retangulo que o C# publica depois de aplicar
                sMain.postDelayed(new Runnable() {
                    @Override public void run() { if (!sDragging) place(); }
                }, 120);
                return true;
        }
        return false;
    }

    private static void flush(long now) {
        if (sPendX == 0 && sPendY == 0) return;
        ShortcutBar.command("grip:" + Math.round(sPendX) + ":" + Math.round(sPendY));
        sPendX = sPendY = 0;
        sLastSend = now;
    }
}
