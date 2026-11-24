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

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        /// <summary>
        /// Update Theme.Scale from a control's window DPI.
        /// .NET Framework 4.8's Control.DeviceDpi can report 96 on a 200%
        /// display (WinForms only fills it in when the app opts into the
        /// WinForms per-monitor DPI mode, which we deliberately do not use
        /// because the shell does its own scaling).  Query the window DPI
        /// directly instead, with a Graphics fallback.
        /// </summary>
        public static void InitFrom(Control c)
        {
            if (c == null) return;
            float s = 0f;
            try
            {
                if (c.IsHandleCreated)
                {
                    uint dpi = GetDpiForWindow(c.Handle);
                    if (dpi > 0) s = dpi / 96f;
                }
            }
            catch { }
            if (s <= 0f)
            {
                try
                {
                    using (var g = Graphics.FromHwnd(c.IsHandleCreated ? c.Handle : IntPtr.Zero))
                        s = g.DpiX / 96f;
                }
                catch { }
            }
            if (s > 0.5f) Scale = s;
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

        // ---- colour scheme --------------------------------------------
        /// <summary>
        /// One complete colour scheme.  Theme exposes the active scheme through
        /// static properties, so all painting code can keep writing Theme.Bg0
        /// etc. and still follow a runtime dark/light switch.
        /// </summary>
        public sealed class PaletteSet
        {
            public Color Bg0, Bg1, Bg2, Bg3, TermBg, TermHeader, SidebarBg, StatusBg;
            public Color Border, BorderStrong, BorderSoft, PaneSplit, FocusLine;
            public Color TextPrimary, TextSecondary, TextTertiary, TextDisabled, TextInverse;
            public Color Accent, AccentHover, AccentPressed, AccentSoft;
            public Color Success, Warning, Danger, Info, Teal, Purple;
            public Color HoverTint, Scrim, CloseHover, HoverRow, ToggleOff;
            public Color[] Ansi16;
        }

        /// <summary>Controls that cache colours on themselves implement this so a
        /// theme switch can refresh them without recreating the window.</summary>
        public interface IThemeAware
        {
            void ApplyThemeColors();
        }

        public static event EventHandler Changed;

        public static bool IsLight { get { return ReferenceEquals(_palette, Light); } }
        public static string CurrentScheme { get { return IsLight ? "light" : "dark"; } }

        private static readonly PaletteSet Dark = CreateDark();
        private static readonly PaletteSet Light = CreateLight();
        private static PaletteSet _palette = Dark;

        /// <summary>Switch the active scheme ("dark" / "light") and notify the UI.</summary>
        public static void SetScheme(string name)
        {
            PaletteSet p = string.Equals(name, "light", StringComparison.OrdinalIgnoreCase)
                ? Light : Dark;
            if (ReferenceEquals(p, _palette)) return;
            _palette = p;
            EventHandler h = Changed;
            if (h != null) h(null, EventArgs.Empty);
        }

        private static PaletteSet CreateDark()
        {
            var p = new PaletteSet();
            p.Bg0 = FromHex("#0F1115");
            p.Bg1 = FromHex("#12151A");
            p.Bg2 = FromHex("#161A21");
            p.Bg3 = FromHex("#1C222B");
            p.TermBg = FromHex("#0B0D11");
            p.TermHeader = FromHex("#101419");
            p.SidebarBg = FromHex("#101319");
            p.StatusBg = FromHex("#0C0F14");
            p.Border = FromHex("#232A34");
            p.BorderStrong = FromHex("#2E3742");
            p.BorderSoft = FromHex("#1B222B");
            p.PaneSplit = FromHex("#272F3A");
            p.FocusLine = FromHex("#3F7AD6");
            p.TextPrimary = FromHex("#E4E8EF");
            p.TextSecondary = FromHex("#9BA5B4");
            p.TextTertiary = FromHex("#697384");
            p.TextDisabled = FromHex("#4A5464");
            p.TextInverse = FromHex("#0B0D11");
            p.Accent = FromHex("#4C8DFF");
            p.AccentHover = FromHex("#6BA3FF");
            p.AccentPressed = FromHex("#3B78E0");
            p.AccentSoft = FromHex("#26374F");
            p.Success = FromHex("#3DD68C");
            p.Warning = FromHex("#E8B339");
            p.Danger = FromHex("#F0564B");
            p.Info = FromHex("#5C9BFF");
            p.Teal = FromHex("#2DD4BF");
            p.Purple = FromHex("#B18CFF");
            p.HoverTint = Color.FromArgb(10, 255, 255, 255);
            p.Scrim = Color.FromArgb(158, 5, 7, 11);
            p.CloseHover = FromHex("#C42B1C");
            p.HoverRow = FromHex("#171D26");
            p.ToggleOff = FromHex("#39424F");
            p.Ansi16 = new[] {
                FromHex("#1A1D23"), FromHex("#F07178"), FromHex("#3DD68C"), FromHex("#E8C547"),
                FromHex("#5C9BFF"), FromHex("#C792EA"), FromHex("#4FD1C5"), FromHex("#C9D1D9"),
                FromHex("#5F6A7B"), FromHex("#FF8A90"), FromHex("#6BEBA8"), FromHex("#F2D97A"),
                FromHex("#8FB8FF"), FromHex("#D9B3FF"), FromHex("#7FE3DA"), FromHex("#EDF1F7"),
            };
            return p;
        }

        private static PaletteSet CreateLight()
        {
            var p = new PaletteSet();
            p.Bg0 = FromHex("#F5F7FB");
            p.Bg1 = FromHex("#EEF2F8");
            p.Bg2 = FromHex("#E8EDF4");
            p.Bg3 = FromHex("#DDE4EE");
            p.TermBg = FromHex("#FBFCFE");
            p.TermHeader = FromHex("#F1F4F9");
            p.SidebarBg = FromHex("#F2F5FA");
            p.StatusBg = FromHex("#EDF1F7");
            p.Border = FromHex("#D6DDE8");
            p.BorderStrong = FromHex("#C0CAD8");
            p.BorderSoft = FromHex("#E3E8F0");
            p.PaneSplit = FromHex("#D7DFE9");
            p.FocusLine = FromHex("#3F7AD6");
            p.TextPrimary = FromHex("#1E2633");
            p.TextSecondary = FromHex("#55637A");
            p.TextTertiary = FromHex("#8893A6");
            p.TextDisabled = FromHex("#B4BDCB");
            p.TextInverse = FromHex("#FFFFFF");
            p.Accent = FromHex("#3B78E0");
            p.AccentHover = FromHex("#5B93F0");
            p.AccentPressed = FromHex("#2F63BF");
            p.AccentSoft = FromHex("#DCE8FF");
            p.Success = FromHex("#1E9E6A");
            p.Warning = FromHex("#C08A16");
            p.Danger = FromHex("#D64545");
            p.Info = FromHex("#3B78E0");
            p.Teal = FromHex("#169B8C");
            p.Purple = FromHex("#7C5CD6");
            p.HoverTint = Color.FromArgb(8, 0, 0, 0);
            p.Scrim = Color.FromArgb(120, 18, 24, 34);
            p.CloseHover = FromHex("#D64545");
            p.HoverRow = FromHex("#E4EAF3");
            p.ToggleOff = FromHex("#C4CEDC");
            p.Ansi16 = new[] {
                FromHex("#3B4453"), FromHex("#C53030"), FromHex("#1E7A4B"), FromHex("#9C6B00"),
                FromHex("#2B6CB0"), FromHex("#8B3FA8"), FromHex("#0F7C82"), FromHex("#8A94A4"),
                FromHex("#8794A6"), FromHex("#E05252"), FromHex("#2FA06A"), FromHex("#C99A2E"),
                FromHex("#4C8DD6"), FromHex("#A566C7"), FromHex("#2AA7AD"), FromHex("#FFFFFF"),
            };
            return p;
        }

        public static Color Bg0 { get { return _palette.Bg0; } }
        public static Color Bg1 { get { return _palette.Bg1; } }
        public static Color Bg2 { get { return _palette.Bg2; } }
        public static Color Bg3 { get { return _palette.Bg3; } }
        public static Color TermBg { get { return _palette.TermBg; } }
        public static Color TermHeader { get { return _palette.TermHeader; } }
        public static Color SidebarBg { get { return _palette.SidebarBg; } }
        public static Color StatusBg { get { return _palette.StatusBg; } }

        public static Color Border { get { return _palette.Border; } }
        public static Color BorderStrong { get { return _palette.BorderStrong; } }
        public static Color BorderSoft { get { return _palette.BorderSoft; } }
        public static Color PaneSplit { get { return _palette.PaneSplit; } }
        public static Color FocusLine { get { return _palette.FocusLine; } }

        public static Color TextPrimary { get { return _palette.TextPrimary; } }
        public static Color TextSecondary { get { return _palette.TextSecondary; } }
        public static Color TextTertiary { get { return _palette.TextTertiary; } }
        public static Color TextDisabled { get { return _palette.TextDisabled; } }
        public static Color TextInverse { get { return _palette.TextInverse; } }

        public static Color Accent { get { return _palette.Accent; } }
        public static Color AccentHover { get { return _palette.AccentHover; } }
        public static Color AccentPressed { get { return _palette.AccentPressed; } }
        public static Color AccentSoft { get { return _palette.AccentSoft; } }

        public static Color Success { get { return _palette.Success; } }
        public static Color Warning { get { return _palette.Warning; } }
        public static Color Danger { get { return _palette.Danger; } }
        public static Color Info { get { return _palette.Info; } }
        public static Color Teal { get { return _palette.Teal; } }
        public static Color Purple { get { return _palette.Purple; } }

        public static Color HoverTint { get { return _palette.HoverTint; } }
        public static Color Scrim { get { return _palette.Scrim; } }
        public static Color CloseHover { get { return _palette.CloseHover; } }
        /// <summary>Hover background for list rows (sidebar sessions / dialog lists).</summary>
        public static Color HoverRow { get { return _palette.HoverRow; } }
        /// <summary>Toggle switch "off" track colour.</summary>
        public static Color ToggleOff { get { return _palette.ToggleOff; } }

        public static Color[] Ansi16 { get { return _palette.Ansi16; } }

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
