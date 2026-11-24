using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace XPutty
{
    /// <summary>
    /// 26px status bar: connection state + session on the left, terminal
    /// capabilities in the centre and latency / crypto / theme on the right.
    /// </summary>
    public class StatusBar : Control
    {
        private bool _connected;
        private bool _error;
        private string _statusText = "未连接";
        private string _sessionName = "无活动会话";
        private string _protocolText = "";
        private string _sizeText = "80x24";
        private string _latency = "12 ms";
        private string _crypto = "AES-256-GCM · SSH-2.0";
        private string _themeName = "深色";
        private PaneView _pane;

        public StatusBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            BackColor = Theme.StatusBg;
            Height = Theme.S(26);
        }

        public PaneView Pane { get { return _pane; } }

        /// <summary>Refresh everything from the active pane.</summary>
        public void SetPane(PaneView pane)
        {
            _pane = pane;
            _error = false;
            if (pane == null)
            {
                _connected = false;
                _statusText = "未连接";
                _sessionName = "无活动会话";
                _protocolText = "";
                _sizeText = "80x24";
            }
            else
            {
                SessionDef s = pane.Session;
                _sessionName = s != null ? s.Name : "未分配会话";
                _connected = pane.Connected;
                _error = !pane.Connected && pane.HasError;
                if (pane.Connected) _statusText = "已连接";
                else if (pane.Session == null) _statusText = "未连接";
                else if (pane.Putty != null && pane.Putty.Started) _statusText = pane.HasError ? "连接失败" : "已断开";
                else _statusText = "连接中";

                if (s == null) _protocolText = "";
                else if (s.Protocol == ProtocolKind.Serial)
                    _protocolText = "SERIAL · " + s.SerialPort;
                else
                {
                    string proto = s.Protocol == ProtocolKind.Telnet ? "TELNET"
                                 : s.Protocol == ProtocolKind.Raw ? "RAW" : "SSH";
                    _protocolText = proto + " · " + (s.Host ?? "") +
                                    (s.Port > 0 ? ":" + s.Port : "");
                }
                _sizeText = EstimateGrid(pane);
            }
            ApplyTheme();
            Invalidate();
        }

        /// <summary>Update without changing the pane (connect/disconnect).</summary>
        public void RefreshState()
        {
            SetPane(_pane);
        }

        public void ApplyTheme()
        {
            _themeName = AppContext.I.Settings != null &&
                         AppContext.I.Settings.Theme == "light" ? "浅色" : "深色";
        }

        public void SetLatency(string text)
        {
            _latency = text;
            Invalidate();
        }

        private static string EstimateGrid(PaneView pane)
        {
            if (pane == null) return "80x24";
            Rectangle area = pane.EmbedClientSize();
            if (area.Width <= 0 || area.Height <= 0) return "80x24";
            // Cascadia Mono advance is about 0.6em, line pitch about 1.25em,
            // at the 13px design size.
            float charW = Theme.SF(13f) * 0.6f;
            float lineH = Theme.SF(20f);
            if (charW < 1) charW = 1;
            if (lineH < 1) lineH = 1;
            int cols = Math.Max(1, (int)(area.Width / charW));
            int rows = Math.Max(1, (int)(area.Height / lineH));
            return cols + "x" + rows;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.StatusBg);

            using (var pen = new Pen(Theme.BorderSoft))
                g.DrawLine(pen, 0, 0, Width, 0);

            int pad = Theme.S(10);
            int midY = Height / 2;

            var fLeft = UiFonts.UI(11f, FontStyle.Regular);
            var fMono = UiFonts.Mono(10.5f, FontStyle.Regular);
            var fSmall = UiFonts.UI(10.5f, FontStyle.Regular);

            // ---- left block -------------------------------------------------
            Color dotColor = _connected ? Theme.Success
                           : _error ? Theme.Danger
                           : (_pane != null && _pane.Session != null && _pane.Putty != null) ? Theme.Warning
                           : Theme.TextDisabled;
            int dotD = Theme.S(6);
            int x = pad;
            if (_connected)
            {
                using (var halo = new SolidBrush(Color.FromArgb(30, dotColor)))
                    g.FillEllipse(halo, x - Theme.S(3), midY - dotD / 2 - Theme.S(3),
                                  dotD + Theme.S(6), dotD + Theme.S(6));
            }
            using (var b = new SolidBrush(dotColor))
                g.FillEllipse(b, x, midY - dotD / 2, dotD, dotD);
            x += dotD + Theme.S(7);

            Color statusColor = _connected ? Theme.TextSecondary
                              : _error ? Theme.Danger : Theme.TextTertiary;
            x = DrawString(g, _statusText, fLeft, statusColor, x, midY, true);
            x = DrawSep(g, x, midY);
            x = DrawString(g, _sessionName, fMono, Theme.TextPrimary, x, midY, true);

            int leftLimit = Width / 3;
            if (!string.IsNullOrEmpty(_protocolText) && x < leftLimit)
            {
                x = DrawSep(g, x, midY);
                DrawString(g, _protocolText, fSmall, Theme.TextTertiary, x, midY, false);
            }

            // ---- centre block ----------------------------------------------
            string centre = "UTF-8  ·  xterm-256color  ·  " + _sizeText + "  ·  LF";
            int centreW = TextRenderer.MeasureText(centre, fMono).Width;
            int centreX = (Width - centreW) / 2;
            if (centreX > x + Theme.S(20))
            {
                Theme.DrawText(g, centre, fMono, Theme.TextTertiary,
                    new Rectangle(centreX, 0, centreW, Height),
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding |
                    TextFormatFlags.NoPrefix);
            }

            // ---- right block -----------------------------------------------
            int rx = Width - pad;

            // theme chip
            int themeW = TextRenderer.MeasureText(_themeName, fSmall).Width;
            rx -= themeW;
            Theme.DrawText(g, _themeName, fSmall, Theme.TextTertiary,
                new Rectangle(rx, 0, themeW, Height),
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            rx = DrawSepLeft(g, rx - Theme.S(8), midY);

            // crypto + lock
            int cryptoW = TextRenderer.MeasureText(_crypto, fSmall).Width;
            rx -= cryptoW;
            Theme.DrawText(g, _crypto, fSmall, Theme.TextTertiary,
                new Rectangle(rx, 0, cryptoW, Height),
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            rx -= Theme.S(6);
            DrawLock(g, new Rectangle(rx - Theme.S(10), midY - Theme.S(5), Theme.S(10), Theme.S(10)),
                     Theme.TextTertiary);
            rx -= Theme.S(10);
            rx = DrawSepLeft(g, rx - Theme.S(6), midY);

            // latency
            Color latColor = LatencyColor(_latency);
            DrawLatencyBars(g, ref rx, midY, latColor);
            int latW = TextRenderer.MeasureText(_latency, fMono).Width;
            rx -= latW;
            Theme.DrawText(g, _latency, fMono, latColor,
                new Rectangle(rx, 0, latW, Height),
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        private static Color LatencyColor(string latency)
        {
            int ms = 0;
            var digits = new string(Array.FindAll((latency ?? "").ToCharArray(), char.IsDigit));
            int.TryParse(digits, out ms);
            if (ms <= 0) return Theme.TextTertiary;
            if (ms < 50) return Theme.Success;
            if (ms <= 150) return Theme.Warning;
            return Theme.Danger;
        }

        private void DrawLatencyBars(Graphics g, ref int right, int midY, Color color)
        {
            int barW = Theme.S(2);
            int gap = Theme.S(2);
            for (int i = 3; i >= 1; i--)
            {
                int h = Theme.S(3 + i * 2);
                right -= barW;
                var r = new Rectangle(right, midY - h / 2, barW, h);
                using (var b = new SolidBrush(i <= 2 ? color : Color.FromArgb(90, color)))
                    g.FillRectangle(b, r);
                right -= gap;
            }
            right -= Theme.S(2);
        }

        private static void DrawLock(Graphics g, Rectangle r, Color c)
        {
            using (var pen = new Pen(c, Math.Max(1f, Theme.SF(1.2f))))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                g.DrawArc(pen, r.X + Theme.S(2), r.Y, r.Width - Theme.S(4), r.Height - Theme.S(3),
                          180, 180);
                var body = new Rectangle(r.X + Theme.S(1), r.Y + r.Height / 2,
                                         r.Width - Theme.S(2), r.Height / 2);
                g.FillRectangle(new SolidBrush(c), body);
            }
        }

        private int DrawString(Graphics g, string text, Font font, Color color, int x, int midY, bool measure)
        {
            if (string.IsNullOrEmpty(text)) return x;
            int w = TextRenderer.MeasureText(text, font).Width;
            Theme.DrawText(g, text, font, color,
                new Rectangle(x, 0, w + Theme.S(2), Height),
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            return x + w;
        }

        private int DrawSep(Graphics g, int x, int midY)
        {
            x += Theme.S(8);
            using (var pen = new Pen(Theme.Border))
                g.DrawLine(pen, x, midY - Theme.S(6), x, midY + Theme.S(6));
            return x + Theme.S(8);
        }

        private int DrawSepLeft(Graphics g, int x, int midY)
        {
            using (var pen = new Pen(Theme.Border))
                g.DrawLine(pen, x, midY - Theme.S(6), x, midY + Theme.S(6));
            return x - Theme.S(8);
        }
    }
}
