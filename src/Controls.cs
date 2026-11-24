// Custom-drawn UI building blocks: theme, icons, cards, pill buttons, chips, search box and a fast list.
// Everything is painted with GDI+/GDI ourselves so the app looks the same everywhere (rounded corners,
// tag badges, hover states) instead of relying on the stock Windows controls.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace TciaDownloader
{
    struct RowState { public bool Selected, Hover, Focus; }

    // ------------------------------------------------------------------ theme

    static class Theme
    {
        public static bool Dark;
        public static float S = 1f;
        public static Color Bg, Surface, Surface2, Surface3, Border, BorderStrong, Text, Text2, Text3,
            Accent, AccentText, Success, Warning, Danger, PrimaryBg, PrimaryFg, PrimaryHover;
        public static Font Base, Small, SmallBold, Semi, Title, Hero, Brand, Input, Badge, Mono;

        public static int Px(float v) { return (int)Math.Round(v * S); }
        public static Color C(int r, int g, int b) { return Color.FromArgb(r, g, b); }

        public static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        public static Color BadgeBg(Color c, Color over) { return Mix(over, c, Dark ? 0.17f : 0.13f); }
        public static Color BadgeFg(Color c) { return Dark ? Mix(c, Color.White, 0.2f) : Mix(c, Color.Black, 0.4f); }

        public static Color ModColor(string m)
        {
            switch (m)
            {
                case "CT": return C(77, 148, 255);
                case "MR": return C(168, 125, 255);
                case "PT": case "NM": return C(255, 146, 82);
                case "RTSTRUCT": return C(76, 183, 130);
                case "RTDOSE": return C(240, 98, 146);
                case "RTPLAN": return C(232, 190, 70);
                case "RTIMAGE": case "RTRECORD": return C(240, 160, 80);
                case "SEG": return C(54, 190, 204);
                case "MG": case "US": case "CR": case "DX": case "XA": case "RF": return C(110, 165, 225);
                default: return C(140, 146, 156);
            }
        }

        public static void Init(float scale)
        {
            S = scale;
            Dark = false;
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    Dark = v is int && (int)v == 0;
                }
            }
            catch (Exception) { }

            if (Dark)
            {
                Bg = C(9, 10, 11); Surface = C(16, 17, 19); Surface2 = C(25, 26, 29); Surface3 = C(34, 36, 40);
                Border = C(36, 38, 43); BorderStrong = C(54, 57, 64);
                Text = C(247, 248, 248); Text2 = C(152, 157, 166); Text3 = C(100, 104, 112);
                Accent = C(94, 106, 210); AccentText = C(146, 157, 255);
                Success = C(76, 183, 130); Warning = C(242, 201, 76); Danger = C(238, 92, 92);
                PrimaryBg = C(247, 248, 248); PrimaryFg = C(9, 10, 11); PrimaryHover = C(222, 224, 229);
            }
            else
            {
                Bg = C(246, 246, 248); Surface = Color.White; Surface2 = C(243, 244, 246); Surface3 = C(233, 235, 239);
                Border = C(227, 228, 232); BorderStrong = C(206, 208, 214);
                Text = C(16, 17, 19); Text2 = C(98, 102, 110); Text3 = C(142, 146, 154);
                Accent = C(94, 106, 210); AccentText = C(72, 82, 186);
                Success = C(28, 148, 90); Warning = C(190, 140, 10); Danger = C(210, 56, 56);
                PrimaryBg = C(17, 17, 19); PrimaryFg = Color.White; PrimaryHover = C(52, 52, 58);
            }

            string text = Has("Segoe UI Variable Text") ? "Segoe UI Variable Text" : "Segoe UI";
            string semi = Has("Segoe UI Variable Text Semibold") ? "Segoe UI Variable Text Semibold" : "Segoe UI Semibold";
            string disp = Has("Segoe UI Variable Display Semib") ? "Segoe UI Variable Display Semib" : semi;
            string mono = Has("Cascadia Mono") ? "Cascadia Mono" : "Consolas";
            Base = new Font(text, 9.5f);
            Small = new Font(text, 8.5f);
            SmallBold = new Font(semi, 8.5f);
            Semi = new Font(semi, 9.5f);
            Title = new Font(semi, 10.5f);
            Hero = new Font(disp, 17f);
            Brand = new Font(disp, 11.5f);
            Input = new Font(text, 10.5f);
            Badge = new Font(semi, 7.5f);
            Mono = new Font(mono, 7f);
        }

        static bool Has(string family)
        {
            try { using (new FontFamily(family)) return true; }
            catch (ArgumentException) { return false; }
        }
    }

    // ------------------------------------------------------------------ drawing helpers

    static class Gfx
    {
        const TextFormatFlags Line = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;

        public static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            rad = Math.Min(rad, Math.Min(r.Width, r.Height) / 2f);
            if (rad <= 0.5f) { p.AddRectangle(r); return p; }
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void Fill(Graphics g, Color c, RectangleF r, float rad)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = Round(r, rad)) using (var b = new SolidBrush(c)) g.FillPath(b, p);
            g.SmoothingMode = old;
        }

        public static void Stroke(Graphics g, Color c, RectangleF r, float rad, float w)
        {
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var rr = new RectangleF(r.X + w / 2, r.Y + w / 2, r.Width - w, r.Height - w);
            using (var p = Round(rr, rad)) using (var pen = new Pen(c, w)) g.DrawPath(pen, p);
            g.SmoothingMode = old;
        }

        public static void Text(Graphics g, string s, Font f, Rectangle r, Color c)
        {
            if (r.Width > 0) TextRenderer.DrawText(g, s ?? "", f, r, c, Line | TextFormatFlags.Left);
        }

        public static void TextRight(Graphics g, string s, Font f, Rectangle r, Color c)
        {
            if (r.Width > 0) TextRenderer.DrawText(g, s ?? "", f, r, c, Line | TextFormatFlags.Right);
        }

        public static void TextCenter(Graphics g, string s, Font f, Rectangle r, Color c)
        {
            if (r.Width > 0) TextRenderer.DrawText(g, s ?? "", f, r, c, Line | TextFormatFlags.HorizontalCenter);
        }

        public static void Wrap(Graphics g, string s, Font f, Rectangle r, Color c)
        {
            if (r.Width > 0)
                TextRenderer.DrawText(g, s ?? "", f, r, c, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.WordBreak |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.TextBoxControl | TextFormatFlags.Left | TextFormatFlags.Top);
        }

        public static int W(string s, Font f)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            return TextRenderer.MeasureText(s, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        }

        // A small coloured tag such as a modality ("CT", "RTDOSE"). Returns its width.
        public static int Badge(Graphics g, string text, int x, int yMid, Color baseColor, Color over, bool alignRight)
        {
            Font f = Theme.Badge;
            int w = W(text, f) + Theme.Px(12), h = Theme.Px(19);
            var r = new Rectangle(alignRight ? x - w : x, yMid - h / 2, w, h);
            Fill(g, Theme.BadgeBg(baseColor, over), r, Theme.Px(5));
            TextRenderer.DrawText(g, text, f, r, Theme.BadgeFg(baseColor),
                TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return w;
        }

        // A rounded label. Returns its width.
        public static int Pill(Graphics g, string text, int x, int yMid, Color bg, Color fg, Font f, int h = 0, string icon = null)
        {
            int ph = h > 0 ? h : Theme.Px(20), pad = Theme.Px(9), isz = Theme.Px(12);
            int iw = icon != null ? isz + Theme.Px(5) : 0;
            int w = pad * 2 + iw + W(text, f);
            var r = new Rectangle(x, yMid - ph / 2, w, ph);
            Fill(g, bg, r, ph / 2f);
            int cx = x + pad;
            if (icon != null) { Icons.Draw(g, icon, new RectangleF(cx, yMid - isz / 2f, isz, isz), fg); cx += iw; }
            TextRenderer.DrawText(g, text, f, new Rectangle(cx, r.Y, w - (cx - x) - pad + 2, ph), fg,
                TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            return w;
        }

        // state: 0 = empty, 1 = ticked, 2 = partly ticked
        public static void Check(Graphics g, Rectangle r, int state, bool hover, Color bg)
        {
            float rad = Theme.Px(4);
            if (state == 0)
            {
                Fill(g, bg, r, rad);
                Stroke(g, hover ? Theme.Text2 : Theme.Text3, r, rad, Math.Max(1f, 1.4f * Theme.S));
                return;
            }
            Fill(g, Theme.Accent, r, rad);
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(Color.White, Math.Max(1.4f, 1.8f * Theme.S)))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                if (state == 1)
                    g.DrawLines(pen, new[] {
                        new PointF(r.X + r.Width * 0.25f, r.Y + r.Height * 0.53f),
                        new PointF(r.X + r.Width * 0.43f, r.Y + r.Height * 0.71f),
                        new PointF(r.X + r.Width * 0.76f, r.Y + r.Height * 0.32f) });
                else
                    g.DrawLine(pen, r.X + r.Width * 0.28f, r.Y + r.Height / 2f, r.Right - r.Width * 0.28f, r.Y + r.Height / 2f);
            }
            g.SmoothingMode = old;
        }

        public static void Bar(Graphics g, Rectangle track, double frac, Color fill, Color back)
        {
            Fill(g, back, track, track.Height / 2f);
            int fw = (int)Math.Round(track.Width * Math.Max(0, Math.Min(1, frac)));
            if (fw > 0) Fill(g, fill, new Rectangle(track.X, track.Y, Math.Max(fw, track.Height), track.Height), track.Height / 2f);
        }
    }

    // ------------------------------------------------------------------ line icons

    static class Icons
    {
        public static void Draw(Graphics g, string name, RectangleF r, Color c)
        {
            if (name == null) return;
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float s = Math.Min(r.Width, r.Height), x0 = r.X + (r.Width - s) / 2, y0 = r.Y + (r.Height - s) / 2;
            Func<float, float, PointF> P = (x, y) => new PointF(x0 + x * s, y0 + y * s);
            using (var pen = new Pen(c, Math.Max(1.25f, s * 0.095f)))
            using (var br = new SolidBrush(c))
            {
                pen.StartCap = pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                switch (name)
                {
                    case "search":
                        g.DrawEllipse(pen, x0 + s * 0.13f, y0 + s * 0.13f, s * 0.56f, s * 0.56f);
                        g.DrawLine(pen, P(0.63f, 0.63f), P(0.88f, 0.88f));
                        break;
                    case "folder":
                        g.DrawLines(pen, new[] { P(0.1f, 0.78f), P(0.1f, 0.24f), P(0.38f, 0.24f), P(0.48f, 0.35f), P(0.9f, 0.35f), P(0.9f, 0.78f), P(0.1f, 0.78f) });
                        break;
                    case "download":
                        g.DrawLine(pen, P(0.5f, 0.12f), P(0.5f, 0.62f));
                        g.DrawLines(pen, new[] { P(0.29f, 0.42f), P(0.5f, 0.63f), P(0.71f, 0.42f) });
                        g.DrawLines(pen, new[] { P(0.14f, 0.68f), P(0.14f, 0.86f), P(0.86f, 0.86f), P(0.86f, 0.68f) });
                        break;
                    case "refresh":
                        g.DrawArc(pen, x0 + s * 0.17f, y0 + s * 0.17f, s * 0.66f, s * 0.66f, -50, 300);
                        g.DrawLines(pen, new[] { P(0.86f, 0.12f), P(0.84f, 0.34f), P(0.62f, 0.31f) });
                        break;
                    case "external":
                        g.DrawLines(pen, new[] { P(0.48f, 0.2f), P(0.2f, 0.2f), P(0.2f, 0.8f), P(0.8f, 0.8f), P(0.8f, 0.52f) });
                        g.DrawLine(pen, P(0.44f, 0.56f), P(0.86f, 0.14f));
                        g.DrawLines(pen, new[] { P(0.6f, 0.14f), P(0.86f, 0.14f), P(0.86f, 0.4f) });
                        break;
                    case "close":
                        g.DrawLine(pen, P(0.27f, 0.27f), P(0.73f, 0.73f));
                        g.DrawLine(pen, P(0.73f, 0.27f), P(0.27f, 0.73f));
                        break;
                    case "chevron":
                        g.DrawLines(pen, new[] { P(0.26f, 0.38f), P(0.5f, 0.62f), P(0.74f, 0.38f) });
                        break;
                    case "chevronUp":
                        g.DrawLines(pen, new[] { P(0.26f, 0.62f), P(0.5f, 0.38f), P(0.74f, 0.62f) });
                        break;
                    case "check":
                        g.DrawLines(pen, new[] { P(0.2f, 0.53f), P(0.41f, 0.74f), P(0.82f, 0.28f) });
                        break;
                    case "file":
                        g.DrawLines(pen, new[] { P(0.56f, 0.1f), P(0.22f, 0.1f), P(0.22f, 0.9f), P(0.78f, 0.9f), P(0.78f, 0.32f), P(0.56f, 0.1f), P(0.56f, 0.32f), P(0.78f, 0.32f) });
                        break;
                    case "user":
                        g.DrawEllipse(pen, x0 + s * 0.33f, y0 + s * 0.12f, s * 0.34f, s * 0.34f);
                        g.DrawArc(pen, x0 + s * 0.15f, y0 + s * 0.56f, s * 0.7f, s * 0.62f, 180, 180);
                        break;
                    case "layers":
                        g.DrawPolygon(pen, new[] { P(0.5f, 0.12f), P(0.88f, 0.32f), P(0.5f, 0.52f), P(0.12f, 0.32f) });
                        g.DrawLines(pen, new[] { P(0.12f, 0.51f), P(0.5f, 0.71f), P(0.88f, 0.51f) });
                        g.DrawLines(pen, new[] { P(0.12f, 0.69f), P(0.5f, 0.89f), P(0.88f, 0.69f) });
                        break;
                    case "inbox":
                        g.DrawLines(pen, new[] { P(0.12f, 0.56f), P(0.12f, 0.86f), P(0.88f, 0.86f), P(0.88f, 0.56f) });
                        g.DrawLines(pen, new[] { P(0.12f, 0.56f), P(0.33f, 0.56f), P(0.4f, 0.67f), P(0.6f, 0.67f), P(0.67f, 0.56f), P(0.88f, 0.56f) });
                        g.DrawLine(pen, P(0.5f, 0.12f), P(0.5f, 0.44f));
                        g.DrawLines(pen, new[] { P(0.36f, 0.31f), P(0.5f, 0.45f), P(0.64f, 0.31f) });
                        break;
                    case "alert":
                        g.DrawEllipse(pen, x0 + s * 0.12f, y0 + s * 0.12f, s * 0.76f, s * 0.76f);
                        g.DrawLine(pen, P(0.5f, 0.3f), P(0.5f, 0.54f));
                        g.FillEllipse(br, x0 + s * 0.44f, y0 + s * 0.64f, s * 0.12f, s * 0.12f);
                        break;
                    case "dot":
                        g.FillEllipse(br, x0 + s * 0.3f, y0 + s * 0.3f, s * 0.4f, s * 0.4f);
                        break;
                }
            }
            g.SmoothingMode = old;
        }
    }

    // ------------------------------------------------------------------ containers

    // A panel that paints its own (optionally rounded, outlined) background.
    class Card : Panel
    {
        public Color Fill = Color.Black, Outer = Color.Black, Stroke = Color.Empty;
        public int Radius;
        public bool LineBottom;

        public Card()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var g = e.Graphics;
            if (Radius <= 0) g.Clear(Fill);
            else
            {
                g.Clear(Outer);
                var r = new RectangleF(0, 0, Width, Height);
                Gfx.Fill(g, Fill, r, Radius);
                if (Stroke != Color.Empty) Gfx.Stroke(g, Stroke, r, Radius, 1);
            }
            if (LineBottom) using (var p = new Pen(Theme.Border)) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
        }
    }

    // ------------------------------------------------------------------ buttons & chips

    enum BtnKind { Primary, Secondary, Ghost }

    class PillButton : Control
    {
        public BtnKind Kind;
        public string Icon, TrailIcon, Badge = null;
        public bool Compact, IconOnly, Square = false;
        bool hover, down;

        public PillButton(string text, BtnKind kind, string icon)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, false);
            Text = text; Kind = kind; Icon = icon;
            Cursor = Cursors.Hand;
            TabStop = true;
            Font = kind == BtnKind.Primary ? Theme.Semi : Theme.Base;
            Height = Theme.Px(36);
        }

        int Pad { get { return Theme.Px(Compact ? 10 : 15); } }

        public int Natural()
        {
            if (IconOnly) return Height;
            int w = Pad * 2 + Gfx.W(Text, Font);
            if (Icon != null) w += Theme.Px(16) + Theme.Px(7);
            if (TrailIcon != null) w += Theme.Px(12) + Theme.Px(6);
            if (!string.IsNullOrEmpty(Badge)) w += Gfx.W(Badge, Theme.Small) + Theme.Px(18) + Theme.Px(7);
            return w;
        }

        public void Fit() { Width = Natural(); }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { down = true; Invalidate(); }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool fire = down && e.Button == MouseButtons.Left && Enabled && ClientRectangle.Contains(e.Location);
            down = false;
            Invalidate();
            base.OnMouseUp(e);
            if (fire) OnClick(EventArgs.Empty);
        }

        protected override bool IsInputKey(Keys k) { return k == Keys.Enter || k == Keys.Space || base.IsInputKey(k); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if ((e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) && Enabled) { e.Handled = true; OnClick(EventArgs.Empty); }
            base.OnKeyDown(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            bool en = Enabled;
            Color bg, fg, border = Color.Empty;
            switch (Kind)
            {
                case BtnKind.Primary:
                    bg = !en ? Theme.Surface3 : down ? Theme.Mix(Theme.PrimaryHover, Theme.PrimaryFg, 0.1f) : hover ? Theme.PrimaryHover : Theme.PrimaryBg;
                    fg = en ? Theme.PrimaryFg : Theme.Text3;
                    break;
                case BtnKind.Secondary:
                    bg = down ? Theme.Surface3 : hover && en ? Theme.Mix(Theme.Surface2, Theme.Surface3, 0.6f) : Theme.Surface2;
                    fg = en ? Theme.Text : Theme.Text3;
                    border = hover && en ? Theme.BorderStrong : Theme.Border;
                    break;
                default:
                    bg = down ? Theme.Surface3 : hover && en ? Theme.Surface2 : BackColor;
                    fg = !en ? Theme.Mix(Theme.Text3, BackColor, 0.35f) : hover ? Theme.Text : Theme.Text2;
                    break;
            }
            float rad = Square ? Theme.Px(8) : Height / 2f;
            var r = new RectangleF(0, 0, Width, Height);
            if (bg != BackColor) Gfx.Fill(g, bg, r, rad);
            if (border != Color.Empty) Gfx.Stroke(g, border, r, rad, 1);
            if (Focused && ShowFocusCues) Gfx.Stroke(g, Theme.Accent, r, rad, Math.Max(1.5f, 1.5f * Theme.S));

            int isz = Theme.Px(16);
            if (IconOnly)
            {
                Icons.Draw(g, Icon, new RectangleF((Width - isz) / 2f, (Height - isz) / 2f, isz, isz), fg);
                return;
            }
            int x = (Width - (Natural() - Pad * 2)) / 2;
            if (Icon != null)
            {
                Icons.Draw(g, Icon, new RectangleF(x, (Height - isz) / 2f, isz, isz), fg);
                x += isz + Theme.Px(7);
            }
            int tw = Gfx.W(Text, Font);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(x, 0, tw + 2, Height), fg,
                TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            x += tw;
            if (!string.IsNullOrEmpty(Badge))
            {
                x += Theme.Px(7);
                x += Gfx.Pill(g, Badge, x, Height / 2, Theme.Accent, Color.White, Theme.Small, Theme.Px(19));
            }
            if (TrailIcon != null)
            {
                int ti = Theme.Px(12);
                Icons.Draw(g, TrailIcon, new RectangleF(x + Theme.Px(6), (Height - ti) / 2f, ti, ti), Kind == BtnKind.Primary ? fg : Theme.Text3);
            }
        }
    }

    // Airbnb-style filter chip: outlined pill, filled when selected.
    class Chip : Control
    {
        public string Code, TrailIcon;
        public bool Selected;
        bool hover, down;

        public Chip()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, false);
            Cursor = Cursors.Hand;
            Font = Theme.Base;
            Height = Theme.Px(32);
        }

        public int Natural() { return Theme.Px(15) * 2 + Gfx.W(Text, Font) + (TrailIcon != null ? Theme.Px(18) : 0); }

        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) down = true; base.OnMouseDown(e); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            bool fire = down && e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location);
            down = false;
            base.OnMouseUp(e);
            if (fire) OnClick(EventArgs.Empty);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            var r = new RectangleF(0, 0, Width, Height);
            Color fg;
            if (Selected)
            {
                Gfx.Fill(g, Theme.Text, r, Height / 2f);
                fg = Theme.Dark ? Theme.Bg : Color.White;
            }
            else
            {
                Gfx.Fill(g, hover ? Theme.Surface2 : Theme.Surface, r, Height / 2f);
                Gfx.Stroke(g, hover ? Theme.BorderStrong : Theme.Border, r, Height / 2f, 1);
                fg = hover ? Theme.Text : Theme.Text2;
            }
            int tw = Gfx.W(Text, Font);
            int x = Theme.Px(15);
            TextRenderer.DrawText(g, Text, Selected ? Theme.Semi : Font, new Rectangle(x, 0, tw + Theme.Px(4), Height), fg,
                TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
            if (TrailIcon != null)
            {
                int ti = Theme.Px(12);
                Icons.Draw(g, TrailIcon, new RectangleF(x + tw + Theme.Px(6), (Height - ti) / 2f, ti, ti), fg);
            }
        }
    }

    // ------------------------------------------------------------------ text input

    // A self-drawn single-line text field (caret, selection, clipboard, word jumps, placeholder).
    // Used instead of the native Edit control so it renders like the rest of the app on every setup.
    class InputBox : Control
    {
        string text = "";
        int caret, anchor, scrollX;
        int[] xs = new int[] { 0 };
        bool caretOn = true, mouseSel;
        readonly System.Windows.Forms.Timer blink;
        string cue = "";

        public InputBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.IBeam;
            blink = new System.Windows.Forms.Timer();
            blink.Interval = Math.Max(350, SystemInformation.CaretBlinkTime);
            blink.Tick += (s, e) => { caretOn = !caretOn; Invalidate(); };
        }

        public string Cue { get { return cue; } set { cue = value ?? ""; Invalidate(); } }

        public override string Text
        {
            get { return text; }
            set
            {
                string v = Clean(value);
                if (v == text) return;
                text = v;
                caret = anchor = text.Length;
                Changed();
            }
        }

        static string Clean(string s) { return (s ?? "").Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' '); }

        bool HasSel { get { return caret != anchor; } }
        int SelMin { get { return Math.Min(caret, anchor); } }
        int SelMax { get { return Math.Max(caret, anchor); } }

        public void SelectAll() { anchor = 0; caret = text.Length; EnsureVisible(); Invalidate(); }
        public void CaretToEnd() { caret = anchor = text.Length; EnsureVisible(); Invalidate(); }

        void Measure()
        {
            xs = new int[text.Length + 1];
            int dot = Gfx.W(".", Font);
            for (int i = 1; i <= text.Length; i++) xs[i] = Gfx.W(text.Substring(0, i) + ".", Font) - dot;
        }

        void Changed()
        {
            Measure();
            EnsureVisible();
            ResetBlink();
            Invalidate();
            OnTextChanged(EventArgs.Empty);
        }

        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Measure(); Invalidate(); }

        void Insert(string s)
        {
            s = Clean(s);
            int a = SelMin;
            text = text.Remove(a, SelMax - a).Insert(a, s);
            caret = anchor = a + s.Length;
            Changed();
        }

        void DeleteRange(int a, int b)
        {
            if (b <= a) return;
            text = text.Remove(a, b - a);
            caret = anchor = a;
            Changed();
        }

        int WordLeft(int i)
        {
            while (i > 0 && !char.IsLetterOrDigit(text[i - 1])) i--;
            while (i > 0 && char.IsLetterOrDigit(text[i - 1])) i--;
            return i;
        }

        int WordRight(int i)
        {
            while (i < text.Length && char.IsLetterOrDigit(text[i])) i++;
            while (i < text.Length && !char.IsLetterOrDigit(text[i])) i++;
            return i;
        }

        void EnsureVisible()
        {
            int w = Math.Max(1, Width - Theme.Px(3));
            int cx = xs[Math.Min(caret, xs.Length - 1)];
            if (cx - scrollX > w) scrollX = cx - w;
            if (cx - scrollX < 0) scrollX = cx;
            scrollX = Math.Max(0, Math.Min(scrollX, Math.Max(0, xs[xs.Length - 1] - w)));
        }

        void ResetBlink()
        {
            caretOn = true;
            if (Focused) { blink.Stop(); blink.Start(); }
        }

        int IndexFromX(int x)
        {
            int px = x + scrollX, best = 0, bestD = int.MaxValue;
            for (int i = 0; i < xs.Length; i++) { int d = Math.Abs(xs[i] - px); if (d < bestD) { bestD = d; best = i; } }
            return best;
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Left: case Keys.Right: case Keys.Home: case Keys.End: case Keys.Delete: case Keys.Back: case Keys.Enter: case Keys.Escape:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled) return;
            bool shift = e.Shift, ctrl = e.Control, handled = true;
            switch (e.KeyCode)
            {
                case Keys.Left:
                    if (!shift && HasSel) caret = SelMin; else caret = ctrl ? WordLeft(caret) : Math.Max(0, caret - 1);
                    if (!shift) anchor = caret;
                    break;
                case Keys.Right:
                    if (!shift && HasSel) caret = SelMax; else caret = ctrl ? WordRight(caret) : Math.Min(text.Length, caret + 1);
                    if (!shift) anchor = caret;
                    break;
                case Keys.Home: caret = 0; if (!shift) anchor = caret; break;
                case Keys.End: caret = text.Length; if (!shift) anchor = caret; break;
                case Keys.Back:
                    if (HasSel) DeleteRange(SelMin, SelMax);
                    else if (caret > 0) DeleteRange(ctrl ? WordLeft(caret) : caret - 1, caret);
                    break;
                case Keys.Delete:
                    if (HasSel) DeleteRange(SelMin, SelMax);
                    else if (caret < text.Length) DeleteRange(caret, ctrl ? WordRight(caret) : caret + 1);
                    break;
                case Keys.A: if (ctrl) SelectAll(); else handled = false; break;
                case Keys.C: if (ctrl) Copy(); else handled = false; break;
                case Keys.X: if (ctrl) { Copy(); DeleteRange(SelMin, SelMax); } else handled = false; break;
                case Keys.V: if (ctrl) Paste(); else handled = false; break;
                case Keys.Insert:
                    if (shift) Paste(); else if (ctrl) Copy(); else handled = false;
                    break;
                default: handled = false; break;
            }
            if (!handled) return;
            e.Handled = true;
            if (ctrl) e.SuppressKeyPress = true;
            EnsureVisible();
            ResetBlink();
            Invalidate();
        }

        void Copy()
        {
            if (!HasSel) return;
            try { Clipboard.SetText(text.Substring(SelMin, SelMax - SelMin)); } catch (Exception) { }
        }

        void Paste()
        {
            try { if (Clipboard.ContainsText()) Insert(Clipboard.GetText().Trim()); } catch (Exception) { }
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            if (e.Handled || char.IsControl(e.KeyChar)) return;
            Insert(e.KeyChar.ToString());
            e.Handled = true;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Focused) Focus();
            if (e.Button != MouseButtons.Left) return;
            caret = IndexFromX(e.X);
            if ((ModifierKeys & Keys.Shift) == 0) anchor = caret;
            mouseSel = true;
            Capture = true;
            ResetBlink();
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!mouseSel) return;
            caret = IndexFromX(e.X);
            EnsureVisible();
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            mouseSel = false;
            Capture = false;
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            int i = IndexFromX(e.X);
            anchor = WordLeft(Math.Min(text.Length, i + (i < text.Length && char.IsLetterOrDigit(text[i]) ? 1 : 0)));
            caret = WordRight(anchor);
            while (caret > anchor && !char.IsLetterOrDigit(text[caret - 1])) caret--;
            Invalidate();
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); ResetBlink(); blink.Start(); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); blink.Stop(); Invalidate(); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); EnsureVisible(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) blink.Dispose();
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            var flags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.Left;
            if (text.Length == 0)
            {
                if (cue != "") TextRenderer.DrawText(g, cue, Font, new Rectangle(0, 0, Width, Height), Theme.Text3, flags | TextFormatFlags.EndEllipsis);
            }
            else
            {
                if (HasSel)
                {
                    int a = xs[SelMin] - scrollX, b = xs[SelMax] - scrollX;
                    var sr = new Rectangle(a, Theme.Px(2), Math.Max(1, b - a), Height - Theme.Px(4));
                    using (var br = new SolidBrush(Focused ? Theme.BadgeBg(Theme.Accent, BackColor) : Theme.Surface3)) g.FillRectangle(br, sr);
                }
                TextRenderer.DrawText(g, text, Font, new Rectangle(-scrollX, 0, xs[xs.Length - 1] + Theme.Px(20), Height), ForeColor, flags);
            }
            if (Focused && caretOn)
            {
                int cx = xs[Math.Min(caret, xs.Length - 1)] - scrollX;
                using (var br = new SolidBrush(Theme.Text)) g.FillRectangle(br, cx, Theme.Px(3), Math.Max(1, (int)Math.Round(Theme.S)), Height - Theme.Px(6));
            }
        }
    }

    // ------------------------------------------------------------------ search box

    class SearchBox : Control
    {
        public readonly InputBox Box;
        public string[] Modes, Cues;
        public int Mode;
        public bool Large;
        public event EventHandler ModeChanged, Submitted, QueryChanged;
        Rectangle[] segs = new Rectangle[0];
        Rectangle clear;
        int hoverSeg = -1;
        bool hoverClear;

        public SearchBox(bool large, string[] modes, string[] cues)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Large = large; Modes = modes; Cues = cues;
            Box = new InputBox();
            Box.Font = large ? Theme.Input : Theme.Base;
            Box.Height = Box.Font.Height + Theme.Px(6);
            Box.BackColor = Theme.Surface2;
            Box.ForeColor = Theme.Text;
            Controls.Add(Box);
            Box.TextChanged += (s, e) => { Invalidate(); if (QueryChanged != null) QueryChanged(this, e); };
            Box.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; if (Submitted != null) Submitted(this, e); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Box.Text = ""; }
            };
            Box.GotFocus += (s, e) => Invalidate();
            Box.LostFocus += (s, e) => Invalidate();
            ApplyCue();
            Cursor = Cursors.IBeam;
        }

        public string Query { get { return Box.Text; } set { Box.Text = value ?? ""; Box.CaretToEnd(); } }

        public void ApplyCue()
        {
            if (Cues != null && Cues.Length > 0) Box.Cue = Cues[Math.Min(Mode, Cues.Length - 1)];
        }

        public void SetMode(int m)
        {
            if (m == Mode) return;
            Mode = m;
            ApplyCue();
            Invalidate();
            if (ModeChanged != null) ModeChanged(this, EventArgs.Empty);
            Box.Focus();
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); DoLayout(); }

        void DoLayout()
        {
            int h = Height;
            int left = Theme.Px(Large ? 44 : 32);
            int right = Width - Theme.Px(Large ? 5 : 6);
            if (Modes != null)
            {
                int sh = h - Theme.Px(10), y = (h - sh) / 2;
                var widths = Modes.Select(m => Gfx.W(m, Theme.Small) + Theme.Px(24)).ToArray();
                int x = right - widths.Sum();
                segs = new Rectangle[Modes.Length];
                for (int i = 0; i < Modes.Length; i++) { segs[i] = new Rectangle(x, y, widths[i], sh); x += widths[i]; }
                right = segs[0].Left - Theme.Px(6);
            }
            int cs = Theme.Px(22);
            clear = new Rectangle(right - cs, (h - cs) / 2, cs, cs);
            right = clear.Left - Theme.Px(4);
            Box.SetBounds(left, (h - Box.Height) / 2 + 1, Math.Max(Theme.Px(20), right - left), Box.Height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            var r = new RectangleF(0, 0, Width, Height);
            Gfx.Fill(g, Theme.Surface2, r, Height / 2f);
            Gfx.Stroke(g, Box.Focused ? Theme.BorderStrong : Theme.Border, r, Height / 2f, 1);
            int isz = Theme.Px(Large ? 17 : 14);
            Icons.Draw(g, "search", new RectangleF(Theme.Px(Large ? 18 : 12), (Height - isz) / 2f, isz, isz), Box.Focused ? Theme.Text2 : Theme.Text3);
            if (Box.Text.Length > 0)
            {
                if (hoverClear) Gfx.Fill(g, Theme.Surface3, clear, clear.Width / 2f);
                int ci = Theme.Px(10);
                Icons.Draw(g, "close", new RectangleF(clear.X + (clear.Width - ci) / 2f, clear.Y + (clear.Height - ci) / 2f, ci, ci), hoverClear ? Theme.Text : Theme.Text3);
            }
            if (Modes != null && segs.Length > 0)
            {
                var all = Rectangle.Union(segs[0], segs[segs.Length - 1]);
                Gfx.Fill(g, Theme.Dark ? Theme.Bg : Theme.Surface3, all, all.Height / 2f);
                for (int i = 0; i < segs.Length; i++)
                {
                    var sr = Rectangle.Inflate(segs[i], -Theme.Px(3), -Theme.Px(3));
                    bool sel = i == Mode;
                    if (sel)
                    {
                        Gfx.Fill(g, Theme.Dark ? Theme.Surface3 : Color.White, sr, sr.Height / 2f);
                        Gfx.Stroke(g, Theme.Border, sr, sr.Height / 2f, 1);
                    }
                    Gfx.TextCenter(g, Modes[i], sel ? Theme.SmallBold : Theme.Small, segs[i], sel ? Theme.Text : hoverSeg == i ? Theme.Text : Theme.Text2);
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hs = -1;
            for (int i = 0; i < segs.Length; i++) if (segs[i].Contains(e.Location)) hs = i;
            bool hc = Box.Text.Length > 0 && clear.Contains(e.Location);
            if (hs != hoverSeg || hc != hoverClear) { hoverSeg = hs; hoverClear = hc; Invalidate(); }
            Cursor = hs >= 0 || hc ? Cursors.Hand : Cursors.IBeam;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverSeg != -1 || hoverClear) { hoverSeg = -1; hoverClear = false; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            for (int i = 0; i < segs.Length; i++) if (segs[i].Contains(e.Location)) { SetMode(i); return; }
            if (Box.Text.Length > 0 && clear.Contains(e.Location)) Box.Text = "";
            Box.Focus();
        }
    }

    // ------------------------------------------------------------------ list

    // A fast owner-drawn list: smooth scrolling, hover, multi-select, keyboard, a thin overlay scrollbar
    // and an empty state. Each list supplies its own row painter.
    class ModernList : Control
    {
        public int RowHeight = 36, HeaderHeight;
        public bool MultiSelect;
        public Func<int, bool> IsGroup = i => false;
        public Action<Graphics, Rectangle, int, RowState> DrawRow;
        public Action<Graphics, Rectangle> DrawHeader;
        public Func<int, Point, bool> HitCheck;
        public event Action<int> CheckClicked, RowActivated, HoverChanged;
        public event Action<int, Point> RowRightClick;
        public event EventHandler SelectionChanged;
        public string EmptyIcon = "search", EmptyTitle = "", EmptyText = "";
        public readonly HashSet<int> Selected = new HashSet<int>();
        int count, scroll, anchor = -1, focusIdx = -1, hover = -1, dragY, dragScroll;
        bool sbHover, sbDrag;

        public ModernList()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
        }

        public int Count
        {
            get { return count; }
            set
            {
                count = Math.Max(0, value);
                Selected.RemoveWhere(i => i >= count);
                if (focusIdx >= count) focusIdx = count - 1;
                if (anchor >= count) anchor = -1;
                if (hover >= count) hover = -1;
                ClampScroll();
                Invalidate();
            }
        }

        public int HoverIndex { get { return hover; } }
        int ViewH { get { return Math.Max(0, Height - HeaderHeight); } }
        int MaxScroll { get { return Math.Max(0, count * RowHeight - ViewH); } }
        void ClampScroll() { scroll = Math.Max(0, Math.Min(scroll, MaxScroll)); }
        public void ScrollToTop() { scroll = 0; Invalidate(); }

        public int IndexAt(Point p)
        {
            if (p.Y < HeaderHeight) return -1;
            int i = (p.Y - HeaderHeight + scroll) / RowHeight;
            return i >= 0 && i < count ? i : -1;
        }

        public Rectangle RowRect(int i) { return new Rectangle(0, HeaderHeight + i * RowHeight - scroll, Width, RowHeight); }

        public void EnsureVisible(int i)
        {
            if (i < 0 || i >= count) return;
            int top = i * RowHeight;
            if (top < scroll) scroll = top;
            else if (top + RowHeight > scroll + ViewH) scroll = top + RowHeight - ViewH;
            ClampScroll();
            Invalidate();
        }

        public List<int> SelectedList() { var l = Selected.ToList(); l.Sort(); return l; }

        public void SilentSelect(int i)
        {
            Selected.Clear();
            if (i >= 0 && i < count) { Selected.Add(i); anchor = focusIdx = i; }
            Invalidate();
        }

        public void SelectOnly(int i) { SilentSelect(i); Raise(); }

        public void ClearSelection()
        {
            if (Selected.Count == 0) return;
            Selected.Clear();
            Invalidate();
            Raise();
        }

        public void SelectAll()
        {
            if (!MultiSelect) return;
            Selected.Clear();
            for (int i = 0; i < count; i++) if (!IsGroup(i)) Selected.Add(i);
            Invalidate();
            Raise();
        }

        void Raise() { if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty); }

        Rectangle ThumbRect()
        {
            int ms = MaxScroll;
            if (ms <= 0) return Rectangle.Empty;
            int vh = ViewH;
            int th = Math.Max(Theme.Px(32), (int)((long)vh * vh / Math.Max(1, count * RowHeight)));
            int ty = HeaderHeight + (int)((long)(vh - th) * scroll / ms);
            int w = Theme.Px(sbHover || sbDrag ? 8 : 5);
            return new Rectangle(Width - w - Theme.Px(3), ty + Theme.Px(3), w, Math.Max(Theme.Px(10), th - Theme.Px(6)));
        }

        bool InScrollZone(Point p) { return MaxScroll > 0 && p.X >= Width - Theme.Px(14) && p.Y >= HeaderHeight; }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            if (count == 0) PaintEmpty(g);
            else if (DrawRow != null)
            {
                int first = scroll / RowHeight, last = Math.Min(count - 1, (scroll + ViewH) / RowHeight);
                g.SetClip(new Rectangle(0, HeaderHeight, Width, ViewH));
                for (int i = first; i <= last; i++)
                {
                    var st = new RowState();
                    st.Selected = Selected.Contains(i);
                    st.Hover = i == hover && !sbDrag;
                    st.Focus = Focused && i == focusIdx;
                    DrawRow(g, RowRect(i), i, st);
                }
                g.ResetClip();
            }
            if (HeaderHeight > 0 && DrawHeader != null)
            {
                using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, 0, 0, Width, HeaderHeight);
                DrawHeader(g, new Rectangle(0, 0, Width, HeaderHeight));
            }
            var tr = ThumbRect();
            if (!tr.IsEmpty) Gfx.Fill(g, sbHover || sbDrag ? Theme.Text3 : Theme.BorderStrong, tr, tr.Width / 2f);
        }

        void PaintEmpty(Graphics g)
        {
            int vh = ViewH;
            if (vh < Theme.Px(60)) return;
            int d = Theme.Px(44);
            int top = HeaderHeight + Math.Max(Theme.Px(8), (vh - d - Theme.Px(70)) / 2);
            var circle = new Rectangle((Width - d) / 2, top, d, d);
            Gfx.Fill(g, Theme.Mix(BackColor, Theme.Text, Theme.Dark ? 0.07f : 0.05f), circle, d / 2f);
            int ic = Theme.Px(20);
            Icons.Draw(g, EmptyIcon, new RectangleF(circle.X + (d - ic) / 2f, circle.Y + (d - ic) / 2f, ic, ic), Theme.Text2);
            var t = new Rectangle(Theme.Px(12), circle.Bottom + Theme.Px(12), Width - Theme.Px(24), Theme.Px(22));
            Gfx.TextCenter(g, EmptyTitle, Theme.Semi, t, Theme.Text);
            var d2 = new Rectangle(Theme.Px(20), t.Bottom + Theme.Px(2), Width - Theme.Px(40), Theme.Px(38));
            TextRenderer.DrawText(g, EmptyText ?? "", Theme.Small, d2, Theme.Text3, TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak |
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.TextBoxControl);
        }

        void SetHover(int h)
        {
            if (h == hover) return;
            hover = h;
            Invalidate();
            if (HoverChanged != null) HoverChanged(h);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (sbDrag)
            {
                var tr = ThumbRect();
                int track = ViewH - tr.Height - Theme.Px(6);
                if (track > 0) { scroll = dragScroll + (int)((long)(e.Y - dragY) * MaxScroll / track); ClampScroll(); Invalidate(); }
                return;
            }
            bool sb = InScrollZone(e.Location);
            if (sb != sbHover) { sbHover = sb; Invalidate(); }
            SetHover(sb ? -1 : IndexAt(e.Location));
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            sbHover = false;
            SetHover(-1);
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Focused) Focus();
            if (e.Button == MouseButtons.Left && InScrollZone(e.Location))
            {
                var tr = ThumbRect();
                if (e.Y >= tr.Top && e.Y <= tr.Bottom) { sbDrag = true; dragY = e.Y; dragScroll = scroll; Capture = true; }
                else { scroll += (e.Y < tr.Top ? -1 : 1) * Math.Max(RowHeight, ViewH - RowHeight); ClampScroll(); Invalidate(); }
                return;
            }
            int i = IndexAt(e.Location);
            if (e.Button == MouseButtons.Right)
            {
                if (i >= 0 && !Selected.Contains(i) && !IsGroup(i)) SelectOnly(i);
                if (RowRightClick != null) RowRightClick(i, e.Location);
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            if (i < 0) { if (MultiSelect) ClearSelection(); return; }
            var rr = RowRect(i);
            if (IsGroup(i) || (HitCheck != null && HitCheck(i, new Point(e.X - rr.X, e.Y - rr.Y))))
            {
                focusIdx = i;
                if (CheckClicked != null) CheckClicked(i);
                Invalidate();
                return;
            }
            bool ctrl = (ModifierKeys & Keys.Control) != 0, shift = (ModifierKeys & Keys.Shift) != 0;
            if (MultiSelect && ctrl) { if (!Selected.Remove(i)) Selected.Add(i); anchor = i; }
            else if (MultiSelect && shift && anchor >= 0)
            {
                Selected.Clear();
                for (int k = Math.Min(anchor, i); k <= Math.Max(anchor, i); k++) if (!IsGroup(k)) Selected.Add(k);
            }
            else { Selected.Clear(); Selected.Add(i); anchor = i; }
            focusIdx = i;
            Invalidate();
            Raise();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (sbDrag) { sbDrag = false; Capture = false; Invalidate(); }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left || InScrollZone(e.Location) || HitCheck != null) return;
            int i = IndexAt(e.Location);
            if (i >= 0 && !IsGroup(i) && RowActivated != null) RowActivated(i);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (MaxScroll <= 0) return;
            scroll -= e.Delta * RowHeight * 3 / 120;
            ClampScroll();
            hover = -2;
            SetHover(IndexAt(e.Location));
            Invalidate();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Up: case Keys.Down: case Keys.PageUp: case Keys.PageDown:
                case Keys.Home: case Keys.End: case Keys.Space: case Keys.Enter:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (count == 0) return;
            int page = Math.Max(1, ViewH / RowHeight - 1), target;
            switch (e.KeyCode)
            {
                case Keys.Up: target = focusIdx - 1; break;
                case Keys.Down: target = focusIdx + 1; break;
                case Keys.PageUp: target = focusIdx - page; break;
                case Keys.PageDown: target = focusIdx + page; break;
                case Keys.Home: target = 0; break;
                case Keys.End: target = count - 1; break;
                case Keys.A:
                    if (e.Control) { SelectAll(); e.Handled = true; }
                    return;
                case Keys.Space:
                    if (focusIdx >= 0 && (HitCheck != null || IsGroup(focusIdx)) && CheckClicked != null) { CheckClicked(focusIdx); Invalidate(); }
                    e.Handled = true;
                    return;
                case Keys.Enter:
                    if (focusIdx >= 0 && RowActivated != null) RowActivated(focusIdx);
                    e.Handled = true;
                    return;
                default:
                    return;
            }
            e.Handled = true;
            if (focusIdx < 0) target = 0;
            target = Math.Max(0, Math.Min(count - 1, target));
            focusIdx = target;
            if (HitCheck == null)
            {
                if (MultiSelect && e.Shift && anchor >= 0)
                {
                    Selected.Clear();
                    for (int k = Math.Min(anchor, target); k <= Math.Max(anchor, target); k++) if (!IsGroup(k)) Selected.Add(k);
                }
                else
                {
                    Selected.Clear();
                    if (!IsGroup(target)) Selected.Add(target);
                    anchor = target;
                }
                Raise();
            }
            EnsureVisible(target);
            Invalidate();
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); ClampScroll(); }
    }

    // ------------------------------------------------------------------ menus

    class MenuColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected { get { return Theme.Surface3; } }
        public override Color MenuItemBorder { get { return Theme.Surface3; } }
        public override Color MenuBorder { get { return Theme.BorderStrong; } }
        public override Color ToolStripDropDownBackground { get { return Theme.Surface2; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Surface2; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Surface2; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Surface2; } }
        public override Color MenuItemSelectedGradientBegin { get { return Theme.Surface3; } }
        public override Color MenuItemSelectedGradientEnd { get { return Theme.Surface3; } }
        public override Color MenuItemPressedGradientBegin { get { return Theme.Surface3; } }
        public override Color MenuItemPressedGradientEnd { get { return Theme.Surface3; } }
        public override Color SeparatorDark { get { return Theme.Border; } }
        public override Color SeparatorLight { get { return Theme.Border; } }
        public override Color CheckBackground { get { return Theme.Surface2; } }
        public override Color CheckSelectedBackground { get { return Theme.Surface3; } }
        public override Color CheckPressedBackground { get { return Theme.Surface3; } }
    }

    class MenuRenderer : ToolStripProfessionalRenderer
    {
        public MenuRenderer() : base(new MenuColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Text3;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var r = e.ImageRectangle;
            int s = Theme.Px(14);
            Icons.Draw(e.Graphics, "check", new RectangleF(r.X + (r.Width - s) / 2f, r.Y + (r.Height - s) / 2f, s, s), Theme.AccentText);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Theme.Text2;
            base.OnRenderArrow(e);
        }
    }
}
