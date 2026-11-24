using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace XPutty
{
    /// <summary>
    /// Design tokens (from design/design-tokens.json) plus GDI+ helpers.
    /// All layout numbers are expressed in 96-DPI logical pixels; use S()
    /// to convert to device pixels for the current window DPI.
    /// </summary>
    public static class Theme
    {
        public static float Scale = 1f;

        public static void InitFrom(Control c)
        {
            if (c != null && c.DeviceDpi > 0)
                Scale = c.DeviceDpi / 96f;
        }

        public static int S(int logical) { return (int)Math.Round(logical * Scale); }
        public static float SF(float logical) { return logical * Scale; }
        // Fonts are sized in DEVICE PIXELS (design px * Scale), not points:
        // on a 200% display a point-sized font would be doubled by Windows
        // on top of our own scaling.
        public static Font UIFont(float designPx, FontStyle st)
        {
            return new Font("Microsoft YaHei UI", SF(designPx), st, GraphicsUnit.Pixel);
        }
        public static Font MonoFont(float designPx, FontStyle st)
        {
            Font f = null;
            try { f = new Font("Cascadia Mono", SF(designPx), st, GraphicsUnit.Pixel); }
            catch { }
            if (f == null) f = new Font("Consolas", SF(designPx), st, GraphicsUnit.Pixel);
            return f;
        }

        // ---- colours ---------------------------------------------------
        public static readonly Color Bg0 = FromHex("#0F1115");
        public static readonly Color Bg1 = FromHex("#12151A");
        public static readonly Color Bg2 = FromHex("#161A21");
        public static readonly Color Bg3 = FromHex("#1C222B");
        public static readonly Color TermBg = FromHex("#0B0D11");
        public static readonly Color TermHeader = FromHex("#101419");
        public static readonly Color SidebarBg = FromHex("#101319");
        public static readonly Color StatusBg = FromHex("#0C0F14");

        public static readonly Color Border = FromHex("#232A34");
        public static readonly Color BorderStrong = FromHex("#2E3742");
        public static readonly Color BorderSoft = FromHex("#1B222B");
        public static readonly Color PaneSplit = FromHex("#272F3A");
        public static readonly Color FocusLine = FromHex("#3F7AD6");

        public static readonly Color TextPrimary = FromHex("#E4E8EF");
        public static readonly Color TextSecondary = FromHex("#9BA5B4");
        public static readonly Color TextTertiary = FromHex("#697384");
        public static readonly Color TextDisabled = FromHex("#4A5464");
        public static readonly Color TextInverse = FromHex("#0B0D11");

        public static readonly Color Accent = FromHex("#4C8DFF");
        public static readonly Color AccentHover = FromHex("#6BA3FF");
        public static readonly Color AccentPressed = FromHex("#3B78E0");
        public static readonly Color AccentSoft = FromHex("#26374F");

        public static readonly Color Success = FromHex("#3DD68C");
        public static readonly Color Warning = FromHex("#E8B339");
        public static readonly Color Danger = FromHex("#F0564B");
        public static readonly Color Info = FromHex("#5C9BFF");
        public static readonly Color Teal = FromHex("#2DD4BF");
        public static readonly Color Purple = FromHex("#B18CFF");

        public static readonly Color HoverTint = Color.FromArgb(10, 255, 255, 255);
        public static readonly Color Scrim = Color.FromArgb(158, 5, 7, 11);
        public static readonly Color CloseHover = FromHex("#C42B1C");

        public static readonly Color[] Ansi16 = {
            FromHex("#1A1D23"), FromHex("#F07178"), FromHex("#3DD68C"), FromHex("#E8C547"),
            FromHex("#5C9BFF"), FromHex("#C792EA"), FromHex("#4FD1C5"), FromHex("#C9D1D9"),
            FromHex("#5F6A7B"), FromHex("#FF8A90"), FromHex("#6BEBA8"), FromHex("#F2D97A"),
            FromHex("#8FB8FF"), FromHex("#D9B3FF"), FromHex("#7FE3DA"), FromHex("#EDF1F7"),
        };

        public static Color FromHex(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return Color.Black;
            hex = hex.TrimStart('#');
            int v = Convert.ToInt32(hex, 16);
            return Color.FromArgb(255, (v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF);
        }

        public static string ToHex(Color c)
        {
            return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
        }

        public static Color Blend(Color a, Color b, float t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * t),
                (int)(a.R + (b.R - a.R) * t),
                (int)(a.G + (b.G - a.G) * t),
                (int)(a.B + (b.B - a.B) * t));
        }

        public static Color ProtocolColor(ProtocolKind p)
        {
            switch (p)
            {
                case ProtocolKind.Ssh: return Info;
                case ProtocolKind.Serial: return Teal;
                case ProtocolKind.Telnet: return Purple;
                default: return TextSecondary;
            }
        }

        // ---- geometric helpers ----------------------------------------
        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            var p = new GraphicsPath();
            int d = Math.Max(0, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            if (d <= 0) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Rectangle r, int radius, Color c)
        {
            using (var path = RoundRect(r, radius))
            using (var b = new SolidBrush(c))
                g.FillPath(b, path);
        }

        public static void DrawRound(Graphics g, Rectangle r, int radius, Color c, float width = 1f)
        {
            using (var path = RoundRect(r, radius))
            using (var pen = new Pen(c, width))
                g.DrawPath(pen, path);
        }

        public static void DrawText(Graphics g, string text, Font f, Color c,
                                    Rectangle bounds, TextFormatFlags flags)
        {
            TextRenderer.DrawText(g, text, f, bounds, c, flags);
        }

        public static void DrawText(Graphics g, string text, Font f, Color c,
                                    int x, int y)
        {
            TextRenderer.DrawText(g, text, f, new Point(x, y), c,
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        // Simple 1.5px line-drawn icons, so we don't need an icon font.
        public static void DrawIcon(Graphics g, string kind, Rectangle r, Color c)
        {
            using (var pen = new Pen(c, Math.Max(1f, SF(1.5f))))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                int x = r.X, y = r.Y, w = r.Width, h = r.Height;
                switch (kind)
                {
                    case "plus":
                        g.DrawLine(pen, x + w / 2, y + S(2), x + w / 2, y + h - S(2));
                        g.DrawLine(pen, x + S(2), y + h / 2, x + w - S(2), y + h / 2);
                        break;
                    case "close":
                        g.DrawLine(pen, x + S(3), y + S(3), x + w - S(3), y + h - S(3));
                        g.DrawLine(pen, x + w - S(3), y + S(3), x + S(3), y + h - S(3));
                        break;
                    case "min":
                        g.DrawLine(pen, x + S(3), y + h / 2, x + w - S(3), y + h / 2);
                        break;
                    case "max":
                        g.DrawRectangle(pen, x + S(3), y + S(3), w - S(6), h - S(6));
                        break;
                    case "restore":
                        g.DrawRectangle(pen, x + S(2), y + S(5), w - S(7), h - S(7));
                        g.DrawRectangle(pen, x + S(5), y + S(2), w - S(7), h - S(7));
                        break;
                    case "split-v":
                        g.DrawRectangle(pen, x + S(2), y + S(2), w - S(4), h - S(4));
                        g.DrawLine(pen, x + w / 2, y + S(2), x + w / 2, y + h - S(2));
                        break;
                    case "split-h":
                        g.DrawRectangle(pen, x + S(2), y + S(2), w - S(4), h - S(4));
                        g.DrawLine(pen, x + S(2), y + h / 2, x + w - S(2), y + h / 2);
                        break;
                    case "gear":
                        g.DrawEllipse(pen, x + w / 2 - S(3), y + h / 2 - S(3), S(6), S(6));
                        g.DrawEllipse(pen, x + S(2), y + S(2), w - S(4), h - S(4));
                        break;
                    case "chevron":
                        g.DrawLines(pen, new Point[] {
                            new Point(x + S(3), y + S(5)),
                            new Point(x + w / 2, y + h - S(5)),
                            new Point(x + w - S(3), y + S(5)) });
                        break;
                    case "search":
                        g.DrawEllipse(pen, x + S(2), y + S(2), S(9), S(9));
                        g.DrawLine(pen, x + S(10), y + S(10), x + w - S(2), y + h - S(2));
                        break;
                    case "host":
                        g.DrawRectangle(pen, x + S(2), y + S(3), w - S(4), h - S(8));
                        g.DrawLine(pen, x + S(2), y + h - S(2), x + w - S(2), y + h - S(2));
                        break;
                    case "key":
                        g.DrawEllipse(pen, x + S(2), y + S(2), S(7), S(7));
                        g.DrawLine(pen, x + S(8), y + S(8), x + w - S(2), y + h - S(2));
                        g.DrawLine(pen, x + w - S(5), y + h - S(2), x + w - S(2), y + h - S(5));
                        break;
                    default:
                        g.DrawEllipse(pen, r);
                        break;
                }
            }
        }
    }
}
