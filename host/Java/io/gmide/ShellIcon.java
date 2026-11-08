package io.gmide;

import android.graphics.Canvas;
import android.graphics.ColorFilter;
import android.graphics.Paint;
import android.graphics.Path;
import android.graphics.PixelFormat;
import android.graphics.Rect;
import android.graphics.RectF;
import android.graphics.drawable.Drawable;

// Icones vetoriais da casca mobile (MobileShell), desenhados num quadro de 24x24 unidades com traco
// arredondado. Sem recursos/PNGs: o jar Java do host nao leva res/.
final class ShellIcon extends Drawable {
    static final int PLAY = 0, STOP = 1, BUG = 2, MORE = 3, CHEVRON_DOWN = 4, BACK = 5, CLOSE = 6,
        CHECK = 7, FOLDER = 8, INSPECTOR = 9, OUTPUT = 10, SEARCH = 11, LAYERS = 12, TABS = 13,
        PLUS = 14, PANEL = 15, DOT = 16, CHEVRON_RIGHT = 17, WINDOW = 18, TOOLS = 19,
        GEAR = 20, SAVE = 21, BROOM = 22, EXPORT = 23, HELP = 24, HOME = 25, TARGET = 26, USER = 27,
        ZOOM_IN = 28, ZOOM_OUT = 29, ZOOM_RESET = 30, FILE = 31, BRANCH = 32, WARNING = 33, CUBE = 34,
        BREAKPOINT = 35;

    private final int mType;
    private final Paint mStroke = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Paint mFill = new Paint(Paint.ANTI_ALIAS_FLAG);
    private final Path mPath = new Path();
    private final RectF mR = new RectF();

    ShellIcon(int type, int color) {
        mType = type;
        mStroke.setStyle(Paint.Style.STROKE);
        mStroke.setStrokeCap(Paint.Cap.ROUND);
        mStroke.setStrokeJoin(Paint.Join.ROUND);
        mFill.setStyle(Paint.Style.FILL);
        setColor(color);
    }

    void setColor(int color) {
        mStroke.setColor(color);
        mFill.setColor(color);
        invalidateSelf();
    }

    @Override
    public void draw(Canvas c) {
        Rect b = getBounds();
        float s = Math.min(b.width(), b.height()) / 24f;
        c.save();
        c.translate(b.left + (b.width() - 24 * s) / 2f, b.top + (b.height() - 24 * s) / 2f);
        c.scale(s, s);
        mStroke.setStrokeWidth(2f);
        Path p = mPath;
        p.reset();
        switch (mType) {
            case PLAY:
                p.moveTo(7, 4.5f); p.lineTo(19, 12); p.lineTo(7, 19.5f); p.close();
                c.drawPath(p, mFill);
                break;
            case STOP:
                mR.set(6, 6, 18, 18);
                c.drawRoundRect(mR, 2, 2, mFill);
                break;
            case BUG:
                mR.set(7.5f, 7, 16.5f, 20);
                c.drawRoundRect(mR, 4.5f, 4.5f, mStroke);
                c.drawLine(12, 11, 12, 20, mStroke);
                c.drawLine(9, 4, 10.5f, 6.5f, mStroke);
                c.drawLine(15, 4, 13.5f, 6.5f, mStroke);
                c.drawLine(4, 11, 7.5f, 12, mStroke);
                c.drawLine(20, 11, 16.5f, 12, mStroke);
                c.drawLine(4, 18, 7.5f, 16.5f, mStroke);
                c.drawLine(20, 18, 16.5f, 16.5f, mStroke);
                break;
            case MORE:
                c.drawCircle(12, 5.5f, 1.8f, mFill);
                c.drawCircle(12, 12, 1.8f, mFill);
                c.drawCircle(12, 18.5f, 1.8f, mFill);
                break;
            case CHEVRON_DOWN:
                p.moveTo(7, 10); p.lineTo(12, 15); p.lineTo(17, 10);
                c.drawPath(p, mStroke);
                break;
            case CHEVRON_RIGHT:
                p.moveTo(10, 7); p.lineTo(15, 12); p.lineTo(10, 17);
                c.drawPath(p, mStroke);
                break;
            case BACK:
                p.moveTo(11, 6); p.lineTo(5, 12); p.lineTo(11, 18);
                p.moveTo(5, 12); p.lineTo(19, 12);
                c.drawPath(p, mStroke);
                break;
            case CLOSE:
                c.drawLine(6.5f, 6.5f, 17.5f, 17.5f, mStroke);
                c.drawLine(17.5f, 6.5f, 6.5f, 17.5f, mStroke);
                break;
            case CHECK:
                p.moveTo(5, 12.5f); p.lineTo(10, 17.5f); p.lineTo(19, 7.5f);
                c.drawPath(p, mStroke);
                break;
            case FOLDER:
                p.moveTo(3.5f, 7); p.lineTo(3.5f, 18.5f); p.lineTo(20.5f, 18.5f); p.lineTo(20.5f, 8.5f);
                p.lineTo(12, 8.5f); p.lineTo(10, 5.5f); p.lineTo(3.5f, 5.5f); p.close();
                c.drawPath(p, mStroke);
                break;
            case INSPECTOR:
                c.drawLine(4, 7, 20, 7, mStroke);
                c.drawLine(4, 12, 20, 12, mStroke);
                c.drawLine(4, 17, 20, 17, mStroke);
                c.drawCircle(15, 7, 2.2f, mFill);
                c.drawCircle(8, 12, 2.2f, mFill);
                c.drawCircle(13, 17, 2.2f, mFill);
                break;
            case OUTPUT:
                mR.set(3.5f, 4.5f, 20.5f, 19.5f);
                c.drawRoundRect(mR, 2, 2, mStroke);
                p.moveTo(7, 9.5f); p.lineTo(10, 12); p.lineTo(7, 14.5f);
                c.drawPath(p, mStroke);
                c.drawLine(12, 15, 17, 15, mStroke);
                break;
            case SEARCH:
                c.drawCircle(10.5f, 10.5f, 5.5f, mStroke);
                c.drawLine(14.5f, 14.5f, 19.5f, 19.5f, mStroke);
                break;
            case LAYERS:
                p.moveTo(12, 4); p.lineTo(20, 8.5f); p.lineTo(12, 13); p.lineTo(4, 8.5f); p.close();
                p.moveTo(4, 12.5f); p.lineTo(12, 17); p.lineTo(20, 12.5f);
                p.moveTo(4, 16); p.lineTo(12, 20.5f); p.lineTo(20, 16);
                c.drawPath(p, mStroke);
                break;
            case TABS:
                mR.set(3.5f, 7.5f, 16.5f, 19.5f);
                c.drawRoundRect(mR, 2, 2, mStroke);
                p.moveTo(7.5f, 4.5f); p.lineTo(20.5f, 4.5f); p.lineTo(20.5f, 15.5f);
                c.drawPath(p, mStroke);
                break;
            case PLUS:
                c.drawLine(12, 5, 12, 19, mStroke);
                c.drawLine(5, 12, 19, 12, mStroke);
                break;
            case DOT:
                c.drawCircle(12, 12, 5, mFill);
                break;
            case WINDOW:
                mR.set(3.5f, 5, 20.5f, 19);
                c.drawRoundRect(mR, 2, 2, mStroke);
                c.drawLine(3.5f, 9, 20.5f, 9, mStroke);
                break;
            case TOOLS:
                mR.set(4, 4, 10.5f, 10.5f); c.drawRoundRect(mR, 1.5f, 1.5f, mStroke);
                mR.set(13.5f, 4, 20, 10.5f); c.drawRoundRect(mR, 1.5f, 1.5f, mStroke);
                mR.set(4, 13.5f, 10.5f, 20); c.drawRoundRect(mR, 1.5f, 1.5f, mStroke);
                mR.set(13.5f, 13.5f, 20, 20); c.drawRoundRect(mR, 1.5f, 1.5f, mStroke);
                break;
            case GEAR:
                c.drawCircle(12, 12, 3, mStroke);
                for (int i = 0; i < 8; i++) {
                    double a = Math.PI / 4 * i;
                    c.drawLine(12 + (float) Math.cos(a) * 6.2f, 12 + (float) Math.sin(a) * 6.2f,
                               12 + (float) Math.cos(a) * 8.6f, 12 + (float) Math.sin(a) * 8.6f, mStroke);
                }
                c.drawCircle(12, 12, 6.2f, mStroke);
                break;
            case SAVE:
                p.moveTo(5, 4.5f); p.lineTo(16, 4.5f); p.lineTo(19.5f, 8); p.lineTo(19.5f, 19.5f);
                p.lineTo(4.5f, 19.5f); p.lineTo(4.5f, 4.5f); p.close();
                c.drawPath(p, mStroke);
                mR.set(8, 4.5f, 15, 9); c.drawRect(mR, mStroke);
                mR.set(7.5f, 13, 16.5f, 19.5f); c.drawRect(mR, mStroke);
                break;
            case BROOM:
                c.drawLine(17, 4, 11.5f, 12.5f, mStroke);
                p.moveTo(8, 11.5f); p.lineTo(15, 14.5f); p.lineTo(12.5f, 20); p.lineTo(4.5f, 18.5f); p.close();
                c.drawPath(p, mStroke);
                c.drawLine(7.5f, 16, 6.5f, 19, mStroke);
                c.drawLine(10.5f, 17, 9.5f, 19.5f, mStroke);
                break;
            case EXPORT:
                p.moveTo(4.5f, 13); p.lineTo(4.5f, 19.5f); p.lineTo(19.5f, 19.5f); p.lineTo(19.5f, 13);
                p.moveTo(12, 15); p.lineTo(12, 4.5f);
                p.moveTo(8, 8.5f); p.lineTo(12, 4.5f); p.lineTo(16, 8.5f);
                c.drawPath(p, mStroke);
                break;
            case HELP:
                c.drawCircle(12, 12, 8.5f, mStroke);
                p.moveTo(9.5f, 9.5f); p.cubicTo(9.5f, 6.5f, 14.5f, 6.5f, 14.5f, 9.5f);
                p.cubicTo(14.5f, 11.5f, 12, 11.5f, 12, 14);
                c.drawPath(p, mStroke);
                c.drawCircle(12, 17, 1.2f, mFill);
                break;
            case HOME:
                p.moveTo(4, 11.5f); p.lineTo(12, 4.5f); p.lineTo(20, 11.5f);
                p.moveTo(6.5f, 10); p.lineTo(6.5f, 19.5f); p.lineTo(17.5f, 19.5f); p.lineTo(17.5f, 10);
                c.drawPath(p, mStroke);
                break;
            case TARGET:
                c.drawCircle(12, 12, 7.5f, mStroke);
                c.drawCircle(12, 12, 2.5f, mFill);
                c.drawLine(12, 2.5f, 12, 6, mStroke);
                c.drawLine(12, 18, 12, 21.5f, mStroke);
                c.drawLine(2.5f, 12, 6, 12, mStroke);
                c.drawLine(18, 12, 21.5f, 12, mStroke);
                break;
            case USER:
                c.drawCircle(12, 8.5f, 3.8f, mStroke);
                p.moveTo(4.5f, 20); p.cubicTo(5.5f, 14.5f, 18.5f, 14.5f, 19.5f, 20);
                c.drawPath(p, mStroke);
                break;
            case ZOOM_IN:
            case ZOOM_OUT:
            case ZOOM_RESET:
                c.drawCircle(10.5f, 10.5f, 6, mStroke);
                c.drawLine(15, 15, 20, 20, mStroke);
                if (mType != ZOOM_RESET) c.drawLine(7.5f, 10.5f, 13.5f, 10.5f, mStroke);
                if (mType == ZOOM_IN) c.drawLine(10.5f, 7.5f, 10.5f, 13.5f, mStroke);
                if (mType == ZOOM_RESET) c.drawCircle(10.5f, 10.5f, 1.5f, mFill);
                break;
            case FILE:
                p.moveTo(6, 3.5f); p.lineTo(14, 3.5f); p.lineTo(18.5f, 8); p.lineTo(18.5f, 20.5f);
                p.lineTo(6, 20.5f); p.close();
                p.moveTo(13.5f, 3.5f); p.lineTo(13.5f, 8.5f); p.lineTo(18.5f, 8.5f);
                c.drawPath(p, mStroke);
                break;
            case BRANCH:
                c.drawCircle(7, 5.5f, 2.2f, mStroke);
                c.drawCircle(7, 18.5f, 2.2f, mStroke);
                c.drawCircle(17, 7.5f, 2.2f, mStroke);
                c.drawLine(7, 7.7f, 7, 16.3f, mStroke);
                p.moveTo(17, 9.7f); p.cubicTo(17, 14, 7, 12, 7, 16.3f);
                c.drawPath(p, mStroke);
                break;
            case WARNING:
                p.moveTo(12, 4); p.lineTo(21, 19.5f); p.lineTo(3, 19.5f); p.close();
                c.drawPath(p, mStroke);
                c.drawLine(12, 10, 12, 14, mStroke);
                c.drawCircle(12, 16.8f, 1.1f, mFill);
                break;
            case CUBE:
                p.moveTo(12, 3.5f); p.lineTo(19.5f, 7.5f); p.lineTo(19.5f, 16.5f); p.lineTo(12, 20.5f);
                p.lineTo(4.5f, 16.5f); p.lineTo(4.5f, 7.5f); p.close();
                p.moveTo(4.5f, 7.5f); p.lineTo(12, 11.5f); p.lineTo(19.5f, 7.5f);
                p.moveTo(12, 11.5f); p.lineTo(12, 20.5f);
                c.drawPath(p, mStroke);
                break;
            case BREAKPOINT:
                c.drawCircle(12, 12, 7.5f, mStroke);
                c.drawCircle(12, 12, 4, mFill);
                break;
            case PANEL:
            default:
                mR.set(4, 4.5f, 20, 19.5f);
                c.drawRoundRect(mR, 2, 2, mStroke);
                c.drawLine(10, 4.5f, 10, 19.5f, mStroke);
                break;
        }
        c.restore();
    }

    @Override public void setAlpha(int alpha) { mStroke.setAlpha(alpha); mFill.setAlpha(alpha); }
    @Override public void setColorFilter(ColorFilter cf) { mStroke.setColorFilter(cf); mFill.setColorFilter(cf); }
    @Override public int getOpacity() { return PixelFormat.TRANSLUCENT; }
}
