using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace XPutty
{
    /// <summary>
    /// Derived surfaces, cached pixel-metric fonts and small GDI+ helpers shared
    /// by every dev-panels control.  Every colour is derived from a Theme token
    /// with Theme.Blend()/Color.FromArgb() so the palette stays in one place, and
    /// every size goes through Theme.S() so 100/125/150/200% DPI stay correct.
    /// Font sizes are DESIGN PIXELS (12.5 -> 12.5px @96dpi, 25px @200dpi).
    /// </summary>
    public static class UiKit
    {
        // ---- surfaces (derived from the active Theme tokens at paint time) ----
        public static Color DialogBg { get { return Theme.Blend(Theme.Bg1, Theme.Bg2, 0.65f); } }
        public static Color ListBg { get { return Theme.Blend(Theme.Bg1, Theme.Bg3, 0.22f); } }
        public static Color FooterBg { get { return ListBg; } }
        public static Color FieldBg { get { return Theme.Blend(Theme.TermBg, Theme.Bg2, 0.22f); } }
        public static Color InputBg { get { return Theme.Blend(Theme.TermBg, Theme.SidebarBg, 0.5f); } }
        public static Color CardBg { get { return Theme.Blend(Theme.Bg1, Theme.Bg3, 0.4f); } }
        public static Color CardBorder { get { return Theme.Border; } }
        public static Color HoverRow { get { return Theme.Blend(Theme.SidebarBg, Theme.Bg3, 0.55f); } }
        public static Color SelectRow { get { return Theme.Blend(Theme.Bg2, Theme.AccentSoft, 0.35f); } }
        public static Color SelectBorder { get { return Theme.Blend(Theme.AccentSoft, Theme.Accent, 0.12f); } }
        public static Color ItemSelBg { get { return Theme.Blend(Theme.Bg3, Theme.AccentSoft, 0.25f); } }
        public static Color ItemSelBorder { get { return Theme.Blend(Theme.AccentSoft, Theme.Accent, 0.1f); } }
        public static Color DashedBorder { get { return Theme.Blend(Theme.Border, Theme.Accent, 0.2f); } }
        public static Color DashedText { get { return Theme.Blend(Theme.Accent, Theme.IsLight ? Color.Black : Color.White, 0.35f); } }
        public static Color KeyCapBorder { get { return Theme.Blend(Theme.Border, Theme.TextSecondary, 0.25f); } }
        public static Color InputBorderColor { get { return Theme.Blend(Theme.Border, Theme.TextTertiary, 0.15f); } }
        public static Color ToggleOff { get { return Theme.Blend(Theme.Bg3, Theme.TextTertiary, 0.35f); } }
        public static Color HeaderDivider { get { return Theme.Border; } }
        public static Color MacroBtnBg { get { return Theme.Bg2; } }
        public static Color MacroBtnBorder { get { return Theme.Border; } }
        public static Color MacroBtnHover { get { return Theme.Blend(Theme.Bg2, Theme.Bg3, 0.85f); } }
        public static Color MacroBtnHoverBorder { get { return Theme.Blend(Theme.Border, Theme.TextSecondary, 0.2f); } }

        public static Color TagColor(string name)
        {
            switch ((name ?? "").Trim().ToLowerInvariant())
            {
                case "blue": return Theme.Info;
                case "green": return Theme.Success;
                case "yellow": return Theme.Warning;
                case "red": return Theme.Danger;
                case "purple": return Theme.Purple;
                case "teal": return Theme.Teal;
                case "orange": return Theme.Blend(Theme.Warning, Theme.Danger, 0.55f);
                case "cyan": return Theme.Ansi16[6];
                case "gray":
                case "grey": return Theme.TextTertiary;
                default: return Theme.Info;
            }
        }

        public static readonly string[] MacroTags = { "blue", "green", "yellow", "red", "purple", "teal" };
        public static readonly string[] AllTags = { "blue", "green", "yellow", "red", "purple", "teal", "orange", "cyan", "gray" };

        public static string TagName(int i)
        {
            return MacroTags[((i % MacroTags.Length) + MacroTags.Length) % MacroTags.Length];
        }

        // ---- fonts (cached; design px, DPI scaled by Theme) -----------
        private static readonly Dictionary<string, Font> Fonts = new Dictionary<string, Font>();

        private static Font GetFont(bool mono, float px, FontStyle st)
        {
            string key = (mono ? "m" : "u") + px.ToString("0.##") + "/" + (int)st;
            Font f;
            if (Fonts.TryGetValue(key, out f)) return f;
            f = mono ? Theme.MonoFont(px, st) : Theme.UIFont(px, st);
            Fonts[key] = f;
            return f;
        }

        public static Font UI(float px) { return GetFont(false, px, FontStyle.Regular); }
        public static Font UIBold(float px) { return GetFont(false, px, FontStyle.Bold); }
        public static Font Mono(float px) { return GetFont(true, px, FontStyle.Regular); }
        public static Font MonoBold(float px) { return GetFont(true, px, FontStyle.Bold); }

        // ---- text ----------------------------------------------------
        public const TextFormatFlags L = TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                                         | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        public const TextFormatFlags LC = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                                          | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        public const TextFormatFlags R = TextFormatFlags.Right | TextFormatFlags.VerticalCenter
                                         | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        public const TextFormatFlags LE = L | TextFormatFlags.EndEllipsis;
        public const TextFormatFlags RE = R | TextFormatFlags.EndEllipsis;

        public static void Text(Graphics g, string s, Font f, Color c, Rectangle r, TextFormatFlags fl)
        {
            if (string.IsNullOrEmpty(s)) return;
            TextRenderer.DrawText(g, s, f, r, c, fl);
        }

        public static void Text(Graphics g, string s, Font f, Color c, int x, int y)
        {
            if (string.IsNullOrEmpty(s)) return;
            TextRenderer.DrawText(g, s, f, new Point(x, y), c,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        public static int Measure(string s, Font f)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            return TextRenderer.MeasureText(s, f, Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
        }

        /// <summary>
        /// GDI+ text draw that honours Graphics.SetClip().  TextRenderer is a
        /// GDI call and ignores a GDI+ clip region, so callers that clip their
        /// painting (e.g. the horizontally scrolling macro strip) must use this
        /// for the partially visible parts.
        /// </summary>
        public static void TextClip(Graphics g, string s, Font f, Color c, Rectangle r)
        {
            if (string.IsNullOrEmpty(s) || r.Width <= 0) return;
            using (var sf = new StringFormat(StringFormatFlags.NoWrap))
            {
                sf.Alignment = StringAlignment.Near;
                sf.LineAlignment = StringAlignment.Center;
                sf.Trimming = StringTrimming.EllipsisCharacter;
                using (var b = new SolidBrush(c))
                    g.DrawString(s, f, b, r, sf);
            }
        }

        public static int TextHeight(Font f)
        {
            return TextRenderer.MeasureText("Ag", f, Size.Empty,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Height;
        }

        // ---- key caps -------------------------------------------------
        public static void KeyCap(Graphics g, Rectangle r, string text, Font f,
                                  Color fg, Color border, Color? bg = null)
        {
            int rad = Theme.S(4);
            if (bg.HasValue) Theme.FillRound(g, r, rad, bg.Value);
            Theme.DrawRound(g, new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), rad, border, 1f);
            Text(g, text, f, fg, r, LC);
        }

        public static Rectangle KeyCapRect(Graphics g, int right, int centerY, string text, Font f)
        {
            int w = Measure(text, f) + Theme.S(10);
            int h = Theme.S(15);
            return new Rectangle(right - w, centerY - h / 2, w, h);
        }

        // ---- misc shapes ----------------------------------------------
        public static void LineH(Graphics g, int x1, int x2, int y, Color c)
        {
            using (var p = new Pen(c, Math.Max(1f, Theme.SF(1f))))
            {
                g.DrawLine(p, x1, y, x2 - 1, y);
            }
        }

        public static void LineV(Graphics g, int x, int y1, int y2, Color c)
        {
            using (var p = new Pen(c, Math.Max(1f, Theme.SF(1f))))
            {
                g.DrawLine(p, x, y1, x, y2 - 1);
            }
        }

        public static void Dot(Graphics g, Point center, int dia, Color c)
        {
            using (var b = new SolidBrush(c))
                g.FillEllipse(b, center.X - dia / 2, center.Y - dia / 2, dia, dia);
        }

        public static void DotHalo(Graphics g, Point center, Color c)
        {
            int halo = Theme.S(10), core = Theme.S(6);
            using (var b = new SolidBrush(Color.FromArgb(48, c)))
                g.FillEllipse(b, center.X - halo / 2, center.Y - halo / 2, halo, halo);
            using (var b = new SolidBrush(c))
                g.FillEllipse(b, center.X - core / 2, center.Y - core / 2, core, core);
        }

        public static void Chevron(Graphics g, Rectangle r, int dir, Color c)
        {
            // dir: 0=down 1=right 2=up 3=left
            float w = Math.Max(1f, Theme.SF(1.4f));
            using (var pen = new Pen(c, w))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                int h = Theme.S(3), v = Theme.S(2);
                Point[] pts;
                switch (dir)
                {
                    case 1: pts = new[] { new Point(cx - v, cy - h), new Point(cx + v, cy), new Point(cx - v, cy + h) }; break;
                    case 2: pts = new[] { new Point(cx - h, cy + v), new Point(cx, cy - v), new Point(cx + h, cy + v) }; break;
                    case 3: pts = new[] { new Point(cx + v, cy - h), new Point(cx - v, cy), new Point(cx + v, cy + h) }; break;
                    default: pts = new[] { new Point(cx - h, cy - v), new Point(cx, cy + v), new Point(cx + h, cy - v) }; break;
                }
                g.DrawLines(pen, pts);
            }
        }

        public static void Check(Graphics g, Rectangle r, Color c)
        {
            using (var pen = new Pen(c, Math.Max(1f, Theme.SF(1.6f))))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                g.DrawLines(pen, new[] {
                    new Point(r.X + r.Width * 22 / 100, r.Y + r.Height * 52 / 100),
                    new Point(r.X + r.Width * 42 / 100, r.Y + r.Height * 72 / 100),
                    new Point(r.X + r.Width * 78 / 100, r.Y + r.Height * 30 / 100) });
            }
        }

        // ---- icons (1.5px stroke line art, GDI+ only) -----------------
        public static void Icon(Graphics g, string kind, Rectangle r, Color c)
        {
            if (string.IsNullOrEmpty(kind)) return;
            float w = Math.Max(1f, Theme.SF(1.5f));
            switch (kind)
            {
                case "check":
                    Check(g, new Rectangle(r.X + Theme.S(2), r.Y + Theme.S(2),
                        r.Width - Theme.S(4), r.Height - Theme.S(4)), c);
                    return;
                case "refresh":
                    using (var pen = new Pen(c, w))
                    {
                        pen.StartCap = LineCap.Round;
                        var rr = new Rectangle(r.X + 2, r.Y + 2, r.Width - 4, r.Height - 4);
                        g.DrawArc(pen, rr, -50f, 285f);
                        int ax = rr.X + rr.Width - Theme.S(1);
                        int ay = rr.Y + Theme.S(3);
                        g.DrawLines(pen, new[] {
                            new Point(ax - Theme.S(4), ay + Theme.S(1)),
                            new Point(ax, ay - Theme.S(2)),
                            new Point(ax + Theme.S(1), ay + Theme.S(3)) });
                    }
                    return;
                case "bulb":
                    using (var pen = new Pen(c, w))
                    {
                        pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                        int x = r.X, y = r.Y, wd = r.Width, ht = r.Height;
                        g.DrawEllipse(pen, x + wd / 4, y + ht / 8, wd / 2, ht / 2);
                        g.DrawLine(pen, x + wd / 2 - wd / 8, y + ht * 5 / 8, x + wd / 2 - wd / 8, y + ht * 7 / 8);
                        g.DrawLine(pen, x + wd / 2 + wd / 8, y + ht * 5 / 8, x + wd / 2 + wd / 8, y + ht * 7 / 8);
                    }
                    return;
                case "keyboard":
                    using (var pen = new Pen(c, Math.Max(1f, Theme.SF(1.3f))))
                    {
                        pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                        var rr = new Rectangle(r.X + 1, r.Y + Theme.S(3),
                            r.Width - 2, r.Height - Theme.S(6));
                        Theme.DrawRound(g, rr, Theme.S(2), c, Math.Max(1f, Theme.SF(1.3f)));
                        using (var b = new SolidBrush(c))
                        {
                            int dot = Math.Max(1, Theme.S(1));
                            for (int row = 0; row < 2; row++)
                            {
                                for (int col = 0; col < 3; col++)
                                {
                                    int dx = rr.X + Theme.S(3) + col * Theme.S(4);
                                    int dy = rr.Y + Theme.S(2) + row * Theme.S(3);
                                    g.FillRectangle(b, dx, dy, dot * 2, dot);
                                }
                            }
                            g.FillRectangle(b, rr.X + Theme.S(4), rr.Bottom - Theme.S(3),
                                rr.Width - Theme.S(8), dot);
                        }
                    }
                    return;
                case "grip":
                    using (var b = new SolidBrush(c))
                    {
                        for (int row = 0; row < 3; row++)
                            for (int col = 0; col < 2; col++)
                                g.FillRectangle(b, r.X + Theme.S(1) + col * Theme.S(4),
                                                r.Y + Theme.S(2) + row * Theme.S(4), Theme.S(2), Theme.S(2));
                    }
                    return;
                case "record":
                    using (var b = new SolidBrush(c))
                        g.FillEllipse(b, r);
                    return;
                case "folder":
                case "folder-open":
                    using (var pen = new Pen(c, w))
                    {
                        pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                        int x = r.X + 1, y = r.Y + Theme.S(4), wd = r.Width - 2, ht = r.Height - Theme.S(6);
                        var path = new GraphicsPath();
                        path.AddLines(new[] {
                            new Point(x, y + Theme.S(3)), new Point(x, y),
                            new Point(x + wd / 3, y), new Point(x + wd / 3 + Theme.S(2), y + Theme.S(3)),
                            new Point(x + wd, y + Theme.S(3)), new Point(x + wd, y + ht),
                            new Point(x, y + ht) });
                        path.CloseFigure();
                        g.DrawPath(pen, path);
                        path.Dispose();
                    }
                    return;
                case "terminal":
                    using (var pen = new Pen(c, w))
                    {
                        pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                        var rr = new Rectangle(r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2);
                        Theme.DrawRound(g, rr, Theme.S(3), c, w);
                        g.DrawLines(pen, new[] {
                            new Point(rr.X + Theme.S(4), rr.Y + Theme.S(4)),
                            new Point(rr.X + Theme.S(7), rr.Y + rr.Height / 2),
                            new Point(rr.X + Theme.S(4), rr.Bottom - Theme.S(4)) });
                        g.DrawLine(pen, rr.X + Theme.S(9), rr.Bottom - Theme.S(4), rr.Right - Theme.S(4), rr.Bottom - Theme.S(4));
                    }
                    return;
                case "plug":
                    using (var pen = new Pen(c, w))
                    {
                        pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                        int cx = r.X + r.Width / 2;
                        // two prongs
                        g.DrawLine(pen, cx - Theme.S(3), r.Y + Theme.S(2), cx - Theme.S(3), r.Y + Theme.S(5));
                        g.DrawLine(pen, cx + Theme.S(3), r.Y + Theme.S(2), cx + Theme.S(3), r.Y + Theme.S(5));
                        // body
                        Theme.DrawRound(g, new Rectangle(cx - Theme.S(5), r.Y + Theme.S(5),
                            Theme.S(10), Theme.S(6)), Theme.S(2), c, w);
                        // cable
                        g.DrawLine(pen, cx, r.Y + Theme.S(11), cx, r.Bottom - Theme.S(2));
                    }
                    return;
                case "sliders":
                    using (var pen = new Pen(c, w))
                    {
                        pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                        int x1 = r.X + 1, x2 = r.Right - 1;
                        for (int i = 0; i < 3; i++)
                        {
                            int y = r.Y + Theme.S(3) + i * (r.Height - Theme.S(6)) / 2;
                            g.DrawLine(pen, x1, y, x2, y);
                            int kx = x1 + (r.Width - 2) * (i == 0 ? 60 : i == 1 ? 35 : 75) / 100;
                            using (var b = new SolidBrush(c))
                                g.FillEllipse(b, kx - Theme.S(2), y - Theme.S(2), Theme.S(4), Theme.S(4));
                        }
                    }
                    return;
                case "download":
                    using (var pen = new Pen(c, w))
                    {
                        pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                        int cx = r.X + r.Width / 2;
                        g.DrawLine(pen, cx, r.Y + 1, cx, r.Bottom - Theme.S(5));
                        g.DrawLines(pen, new[] {
                            new Point(cx - Theme.S(3), r.Bottom - Theme.S(8)),
                            new Point(cx, r.Bottom - Theme.S(5)),
                            new Point(cx + Theme.S(3), r.Bottom - Theme.S(8)) });
                        g.DrawLine(pen, r.X + 1, r.Bottom - 1, r.Right - 1, r.Bottom - 1);
                    }
                    return;
                case "trash":
                    using (var pen = new Pen(c, w))
                    {
                        pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                        g.DrawLine(pen, r.X + 1, r.Y + Theme.S(4), r.Right - 1, r.Y + Theme.S(4));
                        g.DrawLine(pen, r.X + r.Width / 3, r.Y + Theme.S(2), r.X + r.Width * 2 / 3, r.Y + Theme.S(2));
                        g.DrawRectangle(pen, r.X + Theme.S(3), r.Y + Theme.S(5),
                            r.Width - Theme.S(6), r.Height - Theme.S(6));
                    }
                    return;
                case "edit":
                    using (var pen = new Pen(c, w))
                    {
                        pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                        g.DrawLines(pen, new[] {
                            new Point(r.X + 2, r.Bottom - 2),
                            new Point(r.X + 2, r.Bottom - Theme.S(5)),
                            new Point(r.Right - Theme.S(4), r.Y + Theme.S(3)),
                            new Point(r.X + Theme.S(5), r.Y + Theme.S(2)) });
                        g.DrawLine(pen, r.X + 2, r.Bottom - 2, r.X + Theme.S(6), r.Bottom - 2);
                    }
                    return;
                case "copy":
                    using (var pen = new Pen(c, w))
                    {
                        g.DrawRectangle(pen, r.X + 1, r.Y + 1, r.Width - Theme.S(6), r.Height - Theme.S(6));
                        g.DrawRectangle(pen, r.X + Theme.S(5), r.Y + Theme.S(5),
                            r.Width - Theme.S(6), r.Height - Theme.S(6));
                    }
                    return;
                case "save":
                    using (var pen = new Pen(c, w))
                    {
                        var rr = new Rectangle(r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2);
                        Theme.DrawRound(g, rr, Theme.S(2), c, w);
                        g.DrawRectangle(pen, rr.X + Theme.S(3), rr.Y + Theme.S(2), rr.Width - Theme.S(6), Theme.S(5));
                        g.DrawRectangle(pen, rr.X + Theme.S(4), rr.Bottom - Theme.S(6), rr.Width - Theme.S(8), Theme.S(5));
                    }
                    return;
                case "info":
                    using (var pen = new Pen(c, Math.Max(1f, Theme.SF(1.4f))))
                    {
                        pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                        g.DrawEllipse(pen, r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2);
                        int cx = r.X + r.Width / 2;
                        g.DrawLine(pen, cx, r.Y + Theme.S(4), cx, r.Y + Theme.S(5));
                        g.DrawLine(pen, cx, r.Y + Theme.S(7), cx, r.Bottom - Theme.S(4));
                    }
                    return;
                case "lock":
                    using (var pen = new Pen(c, w))
                    {
                        pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                        g.DrawArc(pen, r.X + r.Width / 4, r.Y + 1, r.Width / 2, r.Height / 2, 180, 180);
                        var rr = new Rectangle(r.X + 2, r.Y + r.Height / 2, r.Width - 4, r.Height / 2 - 1);
                        Theme.DrawRound(g, rr, Theme.S(2), c, w);
                    }
                    return;
                case "chevron-left":
                    Chevron(g, r, 3, c);
                    return;
                case "chevron-right":
                    Chevron(g, r, 1, c);
                    return;
                case "chevron-down":
                    Chevron(g, r, 0, c);
                    return;
                case "chevron-up":
                    Chevron(g, r, 2, c);
                    return;
                case "dots":
                    using (var b = new SolidBrush(c))
                    {
                        int cy = r.Y + r.Height / 2;
                        for (int i = 0; i < 3; i++)
                            g.FillEllipse(b, r.X + i * Theme.S(4) + 1, cy - Theme.S(1), Theme.S(2), Theme.S(2));
                    }
                    return;
                case "search":
                case "plus":
                case "close":
                case "min":
                case "max":
                case "gear":
                case "key":
                case "host":
                case "split-v":
                case "split-h":
                case "restore":
                    Theme.DrawIcon(g, kind, r, c);
                    return;
                default:
                    Theme.DrawIcon(g, kind, r, c);
                    return;
            }
        }

        // ---- win32 ----------------------------------------------------
        public const int WM_NCLBUTTONDOWN = 0x00A1;
        public const int HTCAPTION = 2;

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
        [DllImport("user32.dll")]
        public static extern IntPtr SetCapture(IntPtr hWnd);
    }
}
