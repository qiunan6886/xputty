using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace XPutty
{
    /// <summary>
    /// Borderless main window: self drawn 40px title bar with Win11 caption
    /// buttons (Snap Layouts via HTMAXBUTTON), left session sidebar, tab bar,
    /// recursive split area, macro bar and status bar.
    /// </summary>
    public class MainForm : Form
    {
        public const string AppVersion = "v1.0.2";

        // ---- panels (SHELL_SPEC §8 public contract) ------------------------
        public SessionSidebar Sidebar;
        public MacroBar MacroBar;
        public TabBar TabBar;
        public SplitHost SplitHost;
        public StatusBar StatusBar;

        /// <summary>Command line arguments handed over by Program.Main.</summary>
        public string[] StartupArguments;

        // ---- layout tokens (design px) -------------------------------------
        private const int TitleBarHeight = 40;
        private const int TabBarHeight = 38;
        private const int MacroBarHeight = 44;
        private const int StatusBarHeight = 26;
        private const int SidebarWidth = 240;

        // ---- window messages ----------------------------------------------
        private const int WM_NCCALCSIZE = 0x0083;
        private const int WM_NCHITTEST = 0x0084;
        private const int WM_NCACTIVATE = 0x0086;
        private const int WM_GETMINMAXINFO = 0x0024;
        private const int WM_NCMOUSEMOVE = 0x00A0;
        private const int WM_NCMOUSELEAVE = 0x02A2;
        private const int WM_MOUSELEAVE = 0x02A3;
        private const int WM_DPICHANGED = 0x02E0;
        private const int WM_ERASEBKGND = 0x0014;
        private const int WM_SETTINGCHANGE = 0x001A;
        private const int WM_COPYDATA = 0x004A;

        /// <summary>dwData magic for fatal error text sent by the patched putty.exe ('XPTF').</summary>
        private const uint XPUTTY_FATAL_MAGIC = 0x58505446u;

        private const int HTCLIENT = 1;
        private const int HTCAPTION = 2;
        private const int HTMINBUTTON = 8;
        private const int HTMAXBUTTON = 9;
        private const int HTLEFT = 10;
        private const int HTRIGHT = 11;
        private const int HTTOP = 12;
        private const int HTTOPLEFT = 13;
        private const int HTTOPRIGHT = 14;
        private const int HTBOTTOM = 15;
        private const int HTBOTTOMLEFT = 16;
        private const int HTBOTTOMRIGHT = 17;
        private const int HTCLOSE = 20;

        // ---- window state --------------------------------------------------
        private readonly List<TermTab> _tabs = new List<TermTab>();
        private int _activeTab = -1;
        private SessionDef _lastSession;
        private bool _sidebarVisible = true;
        private bool _geometryApplied;
        private bool _dpiChangeSeen;

        private Rectangle _btnMin;
        private Rectangle _btnMax;
        private Rectangle _btnClose;
        private Rectangle _btnSettings;
        private Rectangle _btnCommand;
        private int _hotTitle;

        private readonly List<int> _pendingExitCodes = new List<int>();
        private readonly List<FatalNotice> _pendingFatals = new List<FatalNotice>();
        private Timer _exitTimer;
        private int _exitTimerTicks;

        private sealed class TermTab
        {
            public SplitTree Tree;
            public SplitHost Host;
            public SessionDef Primary;
        }

        private sealed class FatalNotice
        {
            public string Text;
            public DateTime Time;
        }

        public MainForm()
        {
            Text = "xPuTTY";
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Theme.Bg0;
            DoubleBuffered = true;
            KeyPreview = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            MinimumSize = new Size(1, 1);   // real minimum comes from WM_GETMINMAXINFO

            _sidebarVisible = true;
            CreateControls();
            WireEvents();
            Theme.Changed += OnThemeChanged;
        }

        // ==================================================================
        // Theme switching (dark <-> light)
        // ==================================================================

        private void OnThemeChanged(object sender, EventArgs e)
        {
            if (IsDisposed || Disposing) return;
            Diag.Log("theme changed to " + Theme.CurrentScheme);
            BackColor = Theme.Bg0;
            ApplyDwmEffects();
            ApplyThemeToTree(this);
            PerformLayoutCore();
            Invalidate(true);

            // Re-generate the terminal theme INI and push it to every live
            // terminal so the pane colours switch together with the chrome.
            string ini = AppContext.I.BuildThemeIni();
            for (int t = 0; t < _tabs.Count; t++)
            {
                TermTab tab = _tabs[t];
                if (tab == null || tab.Tree == null) continue;
                var panes = tab.Tree.EnumeratePanes();
                for (int i = 0; i < panes.Count; i++)
                {
                    PaneView p = panes[i];
                    if (p != null && p.PuttyHwnd != IntPtr.Zero)
                        PuttyMessages.ApplyTheme(p.PuttyHwnd, ini);
                }
            }
        }

        private static void ApplyThemeToTree(Control root)
        {
            if (root == null) return;
            foreach (Control c in root.Controls)
            {
                var aware = c as Theme.IThemeAware;
                if (aware != null) aware.ApplyThemeColors();
                ApplyThemeToTree(c);
                try { c.Invalidate(); } catch { }
            }
        }

        private void CreateControls()
        {
            TabBar = new TabBar();
            Controls.Add(TabBar);

            Sidebar = new SessionSidebar();
            Controls.Add(Sidebar);

            MacroBar = new MacroBar();
            Controls.Add(MacroBar);

            StatusBar = new StatusBar();
            Controls.Add(StatusBar);
        }

        private void WireEvents()
        {
            TabBar.TabSelected += ActivateTab;
            TabBar.TabCloseRequested += CloseTab;
            TabBar.NewTabRequested += NewTab;
            TabBar.TabMoved += MoveTab;
            TabBar.SplitRequested += SplitActive;
            TabBar.LayoutPresetRequested += ApplyLayoutPreset;
            TabBar.MoreRequested += ShowTabMoreMenu;

            Sidebar.SessionActivated += s => OpenSessionInTab(s, true);
            Sidebar.NewSessionRequested += () => OpenSessionEditor(null);
            Sidebar.ImportPuttyRequested += ImportPuttySessions;
            Sidebar.EditSessionRequested += s => OpenSessionEditor(s);
            Sidebar.DeleteSessionRequested += DeleteSession;
            Sidebar.SerialScanRequested += ScanSerialPorts;
            Sidebar.KeyManagerRequested += ShowKeyManagerInfo;

            MacroBar.MacroClicked += SendMacroToActivePane;
            MacroBar.ConfigureRequested += OpenMacroEditor;
        }

        // ==================================================================
        // Lifecycle
        // ==================================================================

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            InitDpi();
            ApplyDwmEffects();
            if (!_geometryApplied)
            {
                _geometryApplied = true;
                Theme.InitFrom(this);
                UiFonts.Reset();
                RestoreGeometry();
            }
            PerformLayoutCore();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Diag.Log("OnShown client=" + ClientSize.Width + "x" + ClientSize.Height +
                     " scale=" + Theme.Scale);
            PerformLayoutCore();
            RefreshSessions();
            RefreshMacros();
            ApplyStartupArguments();
            if (_tabs.Count == 0) CreateBlankTab(null);
            UpdateTabsUi();
            UpdateStatus();
            Activate();
            SplitHost host = ActiveHost;
            if (host != null) host.EnsurePanesStarted();
            Diag.Log("OnShown done tabs=" + _tabs.Count);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && HasLiveSessions() &&
                AppContext.I.Settings != null && AppContext.I.Settings.CloseBehavior == "ask")
            {
                DialogResult r = MessageBox.Show(this,
                    "还有正在运行的终端会话，确定退出 xPuTTY？\r\n所有会话将被关闭。",
                    "xPuTTY", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (r != DialogResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }
            SaveGeometry();
            AppContext.I.SaveAll();
            PuttyProcess.KillAll();
            base.OnFormClosing(e);
        }

        private bool HasLiveSessions()
        {
            for (int i = 0; i < _tabs.Count; i++)
            {
                var panes = _tabs[i].Tree != null ? _tabs[i].Tree.EnumeratePanes() : new List<PaneView>();
                for (int j = 0; j < panes.Count; j++)
                {
                    if (panes[j].Session != null) return true;
                }
            }
            return false;
        }

        private void InitDpi()
        {
            float scale = 0f;
            try
            {
                uint dpi = GetDpiForWindow(Handle);
                if (dpi > 0) scale = dpi / 96f;
            }
            catch { }
            if (scale <= 0f)
            {
                // Control.DeviceDpi stays at 96 in .NET Framework 4.8 unless
                // PerMonitorV2 is enabled through app.config, so only use it
                // as a last resort.
                try
                {
                    uint dpi = GetDpiForSystem();
                    if (dpi > 0) scale = dpi / 96f;
                }
                catch { }
            }
            if (scale <= 0f) scale = DeviceDpi / 96f;
            if (scale <= 0f) scale = 1f;
            if (Math.Abs(scale - Theme.Scale) > 0.001f)
            {
                Theme.Scale = scale;
                UiFonts.Reset();
            }
        }

        private void RestoreGeometry()
        {
            AppSettings st = AppContext.I.Settings;
            Rectangle work = Screen.PrimaryScreen != null
                ? Screen.PrimaryScreen.WorkingArea
                : new Rectangle(0, 0, 1440, 900);

            int margin = Theme.S(8);
            int maxW = Math.Max(Theme.S(320), work.Width - margin * 2);
            int maxH = Math.Max(Theme.S(240), work.Height - margin * 2);

            // Design baseline is 1600x1000 logical pixels, scaled by DPI and
            // then clamped so it always fits the physical work area (a 200%
            // panel can be smaller than the baseline in device pixels).
            int w = st != null && st.WindowW > 0 ? st.WindowW : Math.Min(Theme.S(1600), maxW);
            int h = st != null && st.WindowH > 0 ? st.WindowH : Math.Min(Theme.S(1000), maxH);
            int minW = Math.Min(Theme.S(1100), maxW);
            int minH = Math.Min(Theme.S(700), maxH);
            w = Math.Max(minW, Math.Min(w, maxW));
            h = Math.Max(minH, Math.Min(h, maxH));

            int x, y;
            if (st != null && st.WindowX != int.MinValue && st.WindowY != int.MinValue)
            {
                x = st.WindowX;
                y = st.WindowY;
                Rectangle candidate = new Rectangle(x, y, w, h);
                bool onScreen = false;
                foreach (Screen sc in Screen.AllScreens)
                    if (sc.WorkingArea.IntersectsWith(candidate)) { onScreen = true; break; }
                if (!onScreen)
                {
                    x = work.X + (work.Width - w) / 2;
                    y = work.Y + (work.Height - h) / 2;
                }
            }
            else
            {
                x = work.X + (work.Width - w) / 2;
                y = work.Y + (work.Height - h) / 2;
            }
            Bounds = new Rectangle(x, y, w, h);
            if (st != null && st.WindowState == 2) WindowState = FormWindowState.Maximized;
        }

        private void SaveGeometry()
        {
            AppSettings st = AppContext.I.Settings;
            if (st == null) return;
            Rectangle b = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            if (b.Width > 0 && b.Height > 0)
            {
                st.WindowX = b.X;
                st.WindowY = b.Y;
                st.WindowW = b.Width;
                st.WindowH = b.Height;
            }
            st.WindowState = WindowState == FormWindowState.Maximized ? 2 : 0;
        }

        private void ApplyDwmEffects()
        {
            try
            {
                int dark = Theme.IsLight ? 0 : 1;
                DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int)); // immersive dark mode
                DwmSetWindowAttribute(Handle, 19, ref dark, sizeof(int)); // older attribute id
                int corner = 2;                                          // DWMWCP_ROUND
                DwmSetWindowAttribute(Handle, 33, ref corner, sizeof(int));
            }
            catch { }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style |= 0x00040000;   // WS_THICKFRAME (resize + snap layouts)
                cp.Style |= 0x00020000;   // WS_MINIMIZEBOX
                cp.Style |= 0x00010000;   // WS_MAXIMIZEBOX
                cp.Style |= 0x00080000;   // WS_SYSMENU
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return cp;
            }
        }

        // ==================================================================
        // Layout
        // ==================================================================

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            PerformLayoutCore();
            InvalidateTitleBar();
        }

        private void PerformLayoutCore()
        {
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            int titleH = Theme.S(TitleBarHeight);
            int tabH = Theme.S(TabBarHeight);
            int macroH = Theme.S(MacroBarHeight);
            int statusH = Theme.S(StatusBarHeight);

            int w = ClientSize.Width;
            int h = ClientSize.Height;
            int sideW = _sidebarVisible && Sidebar != null ? Theme.S(SidebarWidth) : 0;

            int contentTop = titleH;
            int contentBottom = h - statusH;
            int mainX = sideW;
            int mainW = Math.Max(0, w - mainX);

            bool showMacro = AppContext.I.Settings == null || AppContext.I.Settings.ShowMacroBar;
            int macroActual = showMacro ? macroH : 0;

            if (Sidebar != null)
            {
                Sidebar.Visible = _sidebarVisible;
                Sidebar.SetBounds(0, contentTop, sideW, Math.Max(0, contentBottom - contentTop));
            }
            if (StatusBar != null)
                StatusBar.SetBounds(0, contentBottom, w, statusH);
            if (TabBar != null)
                TabBar.SetBounds(mainX, contentTop, mainW, tabH);
            if (MacroBar != null)
            {
                MacroBar.Visible = showMacro;
                MacroBar.SetBounds(mainX, Math.Max(contentTop, contentBottom - macroActual),
                                   mainW, macroActual);
            }

            int splitTop = contentTop + tabH;
            int splitH = Math.Max(0, contentBottom - macroActual - splitTop);
            for (int i = 0; i < _tabs.Count; i++)
            {
                SplitHost host = _tabs[i].Host;
                if (host == null) continue;
                if (host.Bounds.X != mainX || host.Bounds.Y != splitTop ||
                    host.Bounds.Width != mainW || host.Bounds.Height != splitH)
                    host.SetBounds(mainX, splitTop, mainW, splitH);
                host.Visible = i == _activeTab;
            }

            UpdateTitleButtonRects();
        }

        private void UpdateTitleButtonRects()
        {
            int titleH = Theme.S(TitleBarHeight);
            int capW = Theme.S(46);
            _btnClose = new Rectangle(Width - capW, 0, capW, titleH);
            _btnMax = new Rectangle(Width - capW * 2, 0, capW, titleH);
            _btnMin = new Rectangle(Width - capW * 3, 0, capW, titleH);

            int iconW = Theme.S(30), iconH = Theme.S(26);
            int iconY = (titleH - iconH) / 2;
            _btnSettings = new Rectangle(_btnMin.Left - Theme.S(10) - iconW, iconY, iconW, iconH);
            _btnCommand = new Rectangle(_btnSettings.Left - Theme.S(2) - iconW, iconY, iconW, iconH);
        }

        // ==================================================================
        // Painting: title bar
        // ==================================================================

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Bg0);
            DrawTitleBar(g);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // All painting happens in OnPaint (double buffered).
        }

        private void InvalidateTitleBar()
        {
            Invalidate(new Rectangle(0, 0, Width, Theme.S(TitleBarHeight)), false);
        }

        private void DrawTitleBar(Graphics g)
        {
            int h = Theme.S(TitleBarHeight);
            int pad = Theme.S(12);
            bool active = Form.ActiveForm == this;

            using (var b = new SolidBrush(Theme.Bg0))
                g.FillRectangle(b, 0, 0, Width, h);
            using (var pen = new Pen(Theme.BorderSoft))
                g.DrawLine(pen, 0, h - 1, Width, h - 1);

            // ---- brand mark -------------------------------------------------
            int mark = Theme.S(22);
            var markRect = new Rectangle(pad, (h - mark) / 2, mark, mark);
            using (var path = Theme.RoundRect(markRect, Theme.S(6)))
            using (var brush = new LinearGradientBrush(markRect, Theme.Accent, Theme.AccentPressed,
                                                       LinearGradientMode.Vertical))
                g.FillPath(brush, path);
            DrawBrandGlyph(g, markRect, Color.White);

            // ---- app name + version ----------------------------------------
            int x = markRect.Right + Theme.S(8);
            using (var nameFont = UiFonts.UI(13f, FontStyle.Bold))
            {
                int nh = TextRenderer.MeasureText("xPuTTY", nameFont).Height;
                Theme.DrawText(g, "xPuTTY", nameFont, active ? Theme.TextPrimary : Theme.TextSecondary,
                    new Rectangle(x, (h - nh) / 2, Theme.S(90), nh),
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.Left);
                x += TextRenderer.MeasureText("xPuTTY", nameFont).Width + Theme.S(8);
            }
            using (var verFont = UiFonts.Mono(10f, FontStyle.Regular))
            {
                int tw = TextRenderer.MeasureText(AppVersion, verFont).Width;
                int th = TextRenderer.MeasureText(AppVersion, verFont).Height;
                var chip = new Rectangle(x, (h - Theme.S(17)) / 2, tw + Theme.S(10), Theme.S(17));
                Theme.FillRound(g, chip, Theme.S(4), Theme.Bg2);
                Theme.DrawText(g, AppVersion, verFont, Theme.TextTertiary, chip,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                x = chip.Right + Theme.S(12);
            }

            // separator
            using (var pen = new Pen(Theme.BorderSoft))
                g.DrawLine(pen, x, (h - Theme.S(16)) / 2, x, (h + Theme.S(16)) / 2);
            x += Theme.S(12);

            // ---- environment indicator --------------------------------------
            int dotD = Theme.S(6);
            int dotY = h / 2 - dotD / 2;
            using (var halo = new SolidBrush(Color.FromArgb(28, Theme.Success)))
                g.FillEllipse(halo, x - Theme.S(3), dotY - Theme.S(3), dotD + Theme.S(6), dotD + Theme.S(6));
            using (var b = new SolidBrush(Theme.Success))
                g.FillEllipse(b, x, dotY, dotD, dotD);
            x += dotD + Theme.S(8);

            string envName = CurrentEnvironmentName();
            using (var envFont = UiFonts.UI(12f, FontStyle.Bold))
            {
                int ew = TextRenderer.MeasureText(envName, envFont).Width;
                int eh = TextRenderer.MeasureText(envName, envFont).Height;
                Theme.DrawText(g, envName, envFont, active ? Theme.TextPrimary : Theme.TextSecondary,
                    new Rectangle(x, (h - eh) / 2, ew, eh),
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                x += ew + Theme.S(10);
            }
            using (var infoFont = UiFonts.UI(11f, FontStyle.Regular))
            {
                string info = AppContext.I.Sessions.Count + " 个会话 · " + TotalPaneCount() + " 个窗格";
                int iw = TextRenderer.MeasureText(info, infoFont).Width;
                int ih = TextRenderer.MeasureText(info, infoFont).Height;
                int limit = _btnCommand.Left - Theme.S(12);
                if (x + iw < limit)
                {
                    Theme.DrawText(g, info, infoFont, Theme.TextTertiary,
                        new Rectangle(x, (h - ih) / 2, iw, ih),
                        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                }
            }

            // ---- right hand icons -------------------------------------------
            DrawIconButton(g, _btnCommand, "search", _hotTitle == 5);
            DrawIconButton(g, _btnSettings, "gear", _hotTitle == 4);

            // ---- caption buttons --------------------------------------------
            DrawCaptionButton(g, _btnMin, "min", _hotTitle == 1, false);
            DrawCaptionButton(g, _btnMax, WindowState == FormWindowState.Maximized ? "restore" : "max",
                              _hotTitle == 2, false);
            DrawCaptionButton(g, _btnClose, "close", _hotTitle == 3, true);
        }

        private static void DrawBrandGlyph(Graphics g, Rectangle mark, Color color)
        {
            using (var pen = new Pen(color, Math.Max(1f, Theme.SF(1.6f))))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                int x = mark.X + Theme.S(5);
                int y = mark.Y + Theme.S(6);
                int s = Theme.S(6);
                g.DrawLines(pen, new[]
                {
                    new Point(x, y),
                    new Point(x + s, y + s),
                    new Point(x, y + s * 2)
                });
                g.DrawLine(pen, x + s + Theme.S(3), y + s * 2, x + s + Theme.S(9), y + s * 2);
            }
        }

        private void DrawIconButton(Graphics g, Rectangle r, string icon, bool hover)
        {
            if (hover) Theme.FillRound(g, r, Theme.S(4), Theme.Bg3);
            var ir = new Rectangle(r.X + (r.Width - Theme.S(14)) / 2,
                                   r.Y + (r.Height - Theme.S(14)) / 2,
                                   Theme.S(14), Theme.S(14));
            Theme.DrawIcon(g, icon, ir, hover ? Theme.TextPrimary : Theme.TextSecondary);
        }

        private void DrawCaptionButton(Graphics g, Rectangle r, string icon, bool hover, bool isClose)
        {
            if (hover)
            {
                Color bg = isClose ? Theme.CloseHover : Theme.HoverTint;
                using (var b = new SolidBrush(bg))
                    g.FillRectangle(b, r);
            }
            Color fg = hover ? (isClose ? Color.White : Theme.TextPrimary)
                             : (isClose ? Theme.TextSecondary : Theme.TextSecondary);
            var ir = new Rectangle(r.X + (r.Width - Theme.S(14)) / 2,
                                   r.Y + (r.Height - Theme.S(14)) / 2,
                                   Theme.S(14), Theme.S(14));
            Theme.DrawIcon(g, icon, ir, fg);
        }

        private string CurrentEnvironmentName()
        {
            for (int i = 0; i < AppContext.I.Sessions.Count; i++)
            {
                string g = AppContext.I.Sessions[i].Group;
                if (!string.IsNullOrEmpty(g)) return g;
            }
            return "默认环境";
        }

        private int TotalPaneCount()
        {
            int n = 0;
            for (int i = 0; i < _tabs.Count; i++)
                if (_tabs[i].Tree != null) n += _tabs[i].Tree.EnumeratePanes().Count;
            return n;
        }

        // ==================================================================
        // Tabs
        // ==================================================================

        private TermTab ActiveTab
        {
            get
            {
                if (_activeTab < 0 || _activeTab >= _tabs.Count) return null;
                return _tabs[_activeTab];
            }
        }

        private SplitHost ActiveHost
        {
            get
            {
                TermTab t = ActiveTab;
                return t != null ? t.Host : null;
            }
        }

        private PaneView ActivePane
        {
            get
            {
                SplitHost host = ActiveHost;
                return host != null ? host.ActivePane : null;
            }
        }

        private TermTab CreateBlankTab(SessionDef session)
        {
            Diag.Log("CreateBlankTab session=" + (session != null ? session.Name : "(null)") +
                     " puttyExe=" + AppContext.I.PuttyExe);
            var tab = new TermTab();
            tab.Host = new SplitHost();
            tab.Host.ActivePaneChanged += p => OnActivePaneChanged(p);
            var pane = new PaneView(session);
            WirePane(pane, tab);
            tab.Tree = new SplitTree(pane);
            tab.Primary = session;
            tab.Host.Rebuild(tab.Tree);
            tab.Host.Visible = false;
            Controls.Add(tab.Host);
            _tabs.Add(tab);
            ActivateTab(_tabs.Count - 1);
            PerformLayoutCore();
            return tab;
        }

        private void WirePane(PaneView pane, TermTab tab)
        {
            pane.CloseRequested += p => ClosePane(tab, p);
            pane.MaximizeRequested += p =>
            {
                if (tab.Host != null) tab.Host.ToggleMaximize(p);
                UpdateTabsUi();
            };
            pane.Activated += p =>
            {
                if (tab != ActiveTab) return;
                UpdateStatus();
            };
            pane.StateChanged += p =>
            {
                if (tab != ActiveTab) return;
                UpdateStatus();
                UpdateTabsUi();
            };
        }

        public void ActivateTab(int index)
        {
            if (index < 0 || index >= _tabs.Count) return;
            Diag.Log("ActivateTab index=" + index + " hostVisible=" + _tabs[index].Host.Visible);
            _activeTab = index;
            for (int i = 0; i < _tabs.Count; i++)
            {
                if (_tabs[i].Host == null) continue;
                _tabs[i].Host.Visible = i == index;
            }
            SplitHost = _tabs[index].Host;
            PerformLayoutCore();
            SplitHost host = _tabs[index].Host;
            if (host != null)
            {
                host.EnsurePanesStarted();
                PaneView pane = host.ActivePane;
                if (pane == null)
                {
                    var panes = host.Tree != null ? host.Tree.EnumeratePanes() : new List<PaneView>();
                    if (panes.Count > 0) pane = panes[0];
                }
                if (pane != null) host.SetActivePane(pane, false);
                if (pane != null && Visible && Form.ActiveForm == this) pane.FocusTerminal();
            }
            UpdateTabsUi();
            UpdateStatus();
            InvalidateTitleBar();
        }

        private void MoveTab(int from, int to)
        {
            if (from < 0 || from >= _tabs.Count || to < 0 || to >= _tabs.Count || from == to) return;
            TermTab tab = _tabs[from];
            _tabs.RemoveAt(from);
            _tabs.Insert(to, tab);
            if (_activeTab == from) _activeTab = to;
            else if (from < _activeTab && to >= _activeTab) _activeTab--;
            else if (from > _activeTab && to <= _activeTab) _activeTab++;
            UpdateTabsUi();
            UpdateStatus();
        }

        private void CloseTab(int index)
        {
            if (index < 0 || index >= _tabs.Count) return;
            TermTab tab = _tabs[index];
            var panes = tab.Tree != null ? tab.Tree.EnumeratePanes() : new List<PaneView>();

            /*
             * Tear the host down BEFORE the panes: disposing the host while a
             * disposed PaneView is still in its tree lets SplitHost's
             * WM_SHOWWINDOW/VisibleChanged callback run Relayout() against a
             * disposed control (ObjectDisposedException).  Clearing the tree
             * and detaching the panes first keeps every layout pass valid.
             */
            SplitHost host = tab.Host;
            if (host != null)
            {
                tab.Host = null;
                host.Tree = null;
                Controls.Remove(host);
                host.Relayout();
                host.Dispose();
            }

            for (int i = 0; i < panes.Count; i++)
            {
                panes[i].Close();
                panes[i].Dispose();
            }

            _tabs.RemoveAt(index);
            if (_tabs.Count == 0)
            {
                _activeTab = -1;
                SplitHost = null;
                CreateBlankTab(null);
            }
            else
            {
                if (_activeTab >= _tabs.Count) _activeTab = _tabs.Count - 1;
                if (_activeTab > index) _activeTab--;
                if (_activeTab < 0) _activeTab = 0;
                ActivateTab(_activeTab);
            }
            UpdateTabsUi();
            UpdateStatus();
            InvalidateTitleBar();
        }

        private void OnActivePaneChanged(PaneView pane)
        {
            UpdateTabsUi();
            UpdateStatus();
        }

        private void UpdateTabsUi()
        {
            if (TabBar == null) return;
            var infos = new List<TabInfo>();
            for (int i = 0; i < _tabs.Count; i++)
            {
                TermTab tab = _tabs[i];
                var info = new TabInfo();
                var panes = tab.Tree != null ? tab.Tree.EnumeratePanes() : new List<PaneView>();
                PaneView primary = tab.Host != null ? tab.Host.ActivePane : null;
                if (primary == null && panes.Count > 0) primary = panes[0];
                SessionDef s = primary != null ? primary.Session : tab.Primary;
                if (tab.Host != null && tab.Host.MaximizedPane != null) primary = tab.Host.MaximizedPane;
                info.Title = s != null ? s.Name : "新标签";
                info.Protocol = s != null ? s.Protocol : ProtocolKind.Ssh;
                info.Connected = primary != null && primary.Connected;
                info.PaneCount = panes.Count;
                info.Session = s;
                infos.Add(info);
            }
            TabBar.SetTabs(infos, _activeTab);
        }

        private void UpdateStatus()
        {
            if (StatusBar == null) return;
            PaneView pane = ActivePane;
            StatusBar.SetPane(pane);
        }

        // ==================================================================
        // Pane operations (SHELL_SPEC §8 public API)
        // ==================================================================

        public void NewTab()
        {
            SessionDef s = SelectedSidebarSession() ?? _lastSession;
            if (s == null && AppContext.I.Sessions.Count > 0) s = AppContext.I.Sessions[0];
            CreateBlankTab(s);
            UpdateTabsUi();
            UpdateStatus();
        }

        public void OpenSessionInTab(SessionDef s, bool newTab)
        {
            if (s == null)
            {
                NewTab();
                return;
            }
            _lastSession = s;
            if (newTab || ActiveTab == null)
            {
                CreateBlankTab(s);
                UpdateTabsUi();
                UpdateStatus();
                InvalidateTitleBar();
                return;
            }

            TermTab tab = ActiveTab;
            PaneView pane = ActivePane;
            if (pane == null)
            {
                pane = new PaneView(s);
                WirePane(pane, tab);
                tab.Tree = new SplitTree(pane);
                tab.Primary = s;
                tab.Host.Rebuild(tab.Tree);
                pane.EnsureStarted();
            }
            else if (pane.Session == null)
            {
                pane.SetSession(s);
                tab.Primary = s;
            }
            else
            {
                AddPaneToTab(tab, pane, true, s.Clone());
            }
            UpdateTabsUi();
            UpdateStatus();
            InvalidateTitleBar();
        }

        public void CloseActivePane()
        {
            TermTab tab = ActiveTab;
            if (tab == null) return;
            PaneView pane = ActivePane;
            if (pane == null)
            {
                var panes = tab.Tree != null ? tab.Tree.EnumeratePanes() : new List<PaneView>();
                if (panes.Count > 0) pane = panes[0];
            }
            if (pane != null) ClosePane(tab, pane);
        }

        private void ClosePane(TermTab tab, PaneView pane)
        {
            if (tab == null || pane == null) return;
            int tabIndex = _tabs.IndexOf(tab);
            if (tab.Tree == null || tab.Tree.FindLeaf(pane) == null)
            {
                pane.Close();
                pane.Dispose();
                return;
            }
            pane.Close();
            bool empty = tab.Tree.Remove(pane);
            if (empty)
            {
                CloseTab(tabIndex);
                return;
            }
            if (tab.Host != null) tab.Host.Rebuild(tab.Tree);
            pane.Dispose();
            UpdateTabsUi();
            UpdateStatus();
            InvalidateTitleBar();
        }

        public void SplitActive(bool vertical)
        {
            TermTab tab = ActiveTab;
            if (tab == null)
            {
                NewTab();
                return;
            }
            PaneView target = ActivePane;
            if (target == null)
            {
                var panes = tab.Tree != null ? tab.Tree.EnumeratePanes() : new List<PaneView>();
                if (panes.Count == 0) return;
                target = panes[0];
            }
            SessionDef s = target.Session != null ? target.Session.Clone() : PickSessionForNewPane();
            AddPaneToTab(tab, target, vertical, s);
        }

        private SessionDef PickSessionForNewPane()
        {
            SessionDef s = _lastSession ?? SelectedSidebarSession();
            if (s == null && AppContext.I.Sessions.Count > 0) s = AppContext.I.Sessions[0];
            return s != null ? s.Clone() : null;
        }

        private void AddPaneToTab(TermTab tab, PaneView target, bool vertical, SessionDef s)
        {
            if (tab == null || tab.Host == null) return;
            var pane = new PaneView(s);
            WirePane(pane, tab);
            tab.Tree.Split(target, vertical, pane);
            if (tab.Primary == null) tab.Primary = s;
            tab.Host.Rebuild(tab.Tree);
            pane.EnsureStarted();
            tab.Host.SetActivePane(pane, true);
            UpdateTabsUi();
            UpdateStatus();
        }

        public void NextTab(bool forward)
        {
            if (_tabs.Count <= 1) return;
            int next = _activeTab + (forward ? 1 : -1);
            if (next < 0) next = _tabs.Count - 1;
            if (next >= _tabs.Count) next = 0;
            ActivateTab(next);
        }

        public void MaximizeActivePane()
        {
            TermTab tab = ActiveTab;
            if (tab == null || tab.Host == null) return;
            PaneView pane = tab.Host.ActivePane;
            if (pane == null) return;
            tab.Host.ToggleMaximize(pane);
            UpdateTabsUi();
        }

        public void SendMacroToActivePane(MacroDef m)
        {
            if (m == null) return;
            PaneView pane = ActivePane;
            if (pane == null) return;
            string text = PuttyMessages.ExpandMacro(m.Text, pane.Session);
            if (!pane.InjectText(text, m.SendEnter) && StatusBar != null)
                StatusBar.SetLatency("发送失败");
        }

        public void HandleShellHotkey(int vk, bool shift)
        {
            switch (vk)
            {
                case 0x54: NewTab(); break;                    // T
                case 0x57: CloseActivePane(); break;           // W
                case 0x45: SplitActive(true); break;           // E
                case 0x44: SplitActive(false); break;          // D
                case 0x4D: OpenMacroEditor(); break;           // M
                case 0xBC: OpenSettings(); break;              // Ctrl+, (settings)
                case 0x09: NextTab(!shift); break;             // Tab
                case 0x7A: MaximizeActivePane(); break;        // F11
                case 0x4E: OpenSessionEditor(null); break;     // N
                default:
                    if (vk >= 0x31 && vk <= 0x39) TriggerMacro(vk - 0x31);
                    else if (vk >= 0x61 && vk <= 0x69) TriggerMacro(vk - 0x61);
                    break;
            }
        }

        private void TriggerMacro(int index)
        {
            var macros = EnabledMacros();
            if (index < 0 || index >= macros.Count) return;
            SendMacroToActivePane(macros[index]);
        }

        private List<MacroDef> EnabledMacros()
        {
            var list = new List<MacroDef>();
            for (int i = 0; i < AppContext.I.Macros.Count; i++)
                if (AppContext.I.Macros[i].Enabled) list.Add(AppContext.I.Macros[i]);
            list.Sort((a, b) => a.Sort.CompareTo(b.Sort));
            return list;
        }

        // ==================================================================
        // Layout presets
        // ==================================================================

        public void ApplyLayoutPreset(LayoutPreset preset)
        {
            TermTab tab = ActiveTab;
            if (tab == null || tab.Host == null || tab.Tree == null) return;

            var panes = tab.Tree.EnumeratePanes();
            if (panes.Count == 0) return;
            PaneView active = tab.Host.ActivePane ?? panes[0];
            panes.Remove(active);
            panes.Insert(0, active);

            var created = new List<PaneView>();
            Func<int, PaneView> factory = index =>
            {
                SessionDef s = active.Session != null ? active.Session.Clone() : PickSessionForNewPane();
                var p = new PaneView(s);
                WirePane(p, tab);
                created.Add(p);
                return p;
            };

            SplitTree newTree = SplitTree.BuildPreset(preset, panes, factory);
            var kept = new HashSet<PaneView>(newTree.EnumeratePanes());
            for (int i = 0; i < panes.Count; i++)
            {
                if (kept.Contains(panes[i])) continue;
                panes[i].Close();
                panes[i].Dispose();
            }
            tab.Tree = newTree;
            tab.Host.Rebuild(newTree);
            tab.Host.EnsurePanesStarted();
            UpdateTabsUi();
            UpdateStatus();
        }

        // ==================================================================
        // Dialogs / data
        // ==================================================================

        public void OpenMacroEditor()
        {
            var clones = new List<MacroDef>();
            for (int i = 0; i < AppContext.I.Macros.Count; i++) clones.Add(AppContext.I.Macros[i].Clone());
            using (var form = new MacroEditorForm(clones))
            {
                if (form.ShowDialog(this) == DialogResult.OK && form.Accepted)
                {
                    AppContext.I.Macros = clones;
                    AppContext.I.ReindexMacros();
                    AppContext.I.SaveAll();
                    RefreshMacros();
                }
            }
        }

        public void OpenSessionEditor(SessionDef s)
        {
            if (s == null)
            {
                using (var form = new SessionEditorForm(null))
                {
                    if (form.ShowDialog(this) == DialogResult.OK && form.Accepted)
                    {
                        SessionDef created = ExtractSession(form);
                        if (created == null) created = FallbackNewSession();
                        if (created != null)
                        {
                            if (AppContext.I.Sessions.FindIndex(x => x.Id == created.Id) < 0)
                                AppContext.I.Sessions.Add(created);
                            AppContext.I.SaveAll();
                            RefreshSessions();
                        }
                    }
                }
            }
            else
            {
                SessionDef draft = s.Clone();
                using (var form = new SessionEditorForm(draft))
                {
                    if (form.ShowDialog(this) == DialogResult.OK && form.Accepted)
                    {
                        SessionDef edited = ExtractSession(form) ?? draft;
                        int index = AppContext.I.Sessions.FindIndex(x => x.Id == s.Id);
                        if (index >= 0) AppContext.I.Sessions[index] = edited;
                        else AppContext.I.Sessions.Add(edited);
                        AppContext.I.SaveAll();
                        RefreshSessions();
                    }
                }
            }
        }

        private SessionDef FallbackNewSession()
        {
            var s = new SessionDef();
            s.Name = "新会话 " + (AppContext.I.Sessions.Count + 1);
            return s;
        }

        private static SessionDef ExtractSession(SessionEditorForm form)
        {
            try
            {
                var prop = form.GetType().GetProperty("Session");
                if (prop != null) return prop.GetValue(form, null) as SessionDef;
            }
            catch { }
            return null;
        }

        public void OpenSettings()
        {
            AppSettings draft = CopySettings(AppContext.I.Settings);
            using (var form = new SettingsForm(draft))
            {
                if (form.ShowDialog(this) == DialogResult.OK && form.Accepted)
                {
                    string oldTheme = AppContext.I.Settings.Theme;
                    AppContext.I.Settings = draft;
                    _sidebarVisible = draft.ShowSidebar;
                    if (!string.Equals(oldTheme ?? "dark", draft.Theme ?? "dark",
                                       StringComparison.OrdinalIgnoreCase))
                    {
                        Theme.SetScheme(draft.Theme);
                        ApplyDwmEffects();
                    }
                    AppContext.I.BuildThemeIni();
                    PerformLayoutCore();
                    UpdateStatus();
                    AppContext.I.SaveAll();
                    Invalidate(true);
                }
            }
        }

        private static AppSettings CopySettings(AppSettings src)
        {
            var d = new AppSettings();
            if (src == null) return d;
            d.Theme = src.Theme;
            d.Accent = src.Accent;
            d.FontSize = src.FontSize;
            d.FontName = src.FontName;
            d.ShowSidebar = src.ShowSidebar;
            d.ShowMacroBar = src.ShowMacroBar;
            d.RestoreLastLayout = src.RestoreLastLayout;
            d.CloseBehavior = src.CloseBehavior;
            d.WindowX = src.WindowX;
            d.WindowY = src.WindowY;
            d.WindowW = src.WindowW;
            d.WindowH = src.WindowH;
            d.WindowState = src.WindowState;
            return d;
        }

        public void RefreshSessions()
        {
            if (Sidebar == null) return;
            Sidebar.SetSessions(AppContext.I.Sessions, null);
            Sidebar.InvalidateList();
        }

        public void RefreshMacros()
        {
            if (MacroBar == null) return;
            MacroBar.SetMacros(EnabledMacros());
        }

        public void ToggleSidebar()
        {
            _sidebarVisible = !_sidebarVisible;
            if (AppContext.I.Settings != null) AppContext.I.Settings.ShowSidebar = _sidebarVisible;
            PerformLayoutCore();
            AppContext.I.SaveAll();
        }

        private void ImportPuttySessions()
        {
            int n = AppContext.I.ImportPuttySessions();
            RefreshSessions();
            if (n > 0)
            {
                MessageBox.Show(this, "已从 PuTTY 导入 " + n + " 个会话。", "xPuTTY",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show(this, "没有找到可导入的 PuTTY 会话，或会话已存在。", "xPuTTY",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void DeleteSession(SessionDef s)
        {
            if (s == null) return;
            DialogResult r = MessageBox.Show(this, "删除会话「" + s.Name + "」？\n该操作不可撤销。",
                "xPuTTY", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (r != DialogResult.Yes) return;
            AppContext.I.DeleteSession(s.Id);
            AppContext.I.SaveAll();
            RefreshSessions();
        }

        /// <summary>Sidebar footer: list the serial ports the OS reports.</summary>
        private void ScanSerialPorts()
        {
            string[] ports = null;
            try { ports = System.IO.Ports.SerialPort.GetPortNames(); }
            catch { }
            string text;
            if (ports != null && ports.Length > 0)
            {
                Array.Sort(ports);
                text = "检测到 " + ports.Length + " 个串口：\r\n\r\n" +
                       string.Join("   ", ports) +
                       "\r\n\r\n在「新建会话」中选择串口协议即可使用。";
            }
            else
            {
                text = "未检测到可用串口。\r\n\r\n请确认设备已连接、驱动已安装，" +
                       "并在设备管理器中查看端口号。";
            }
            MessageBox.Show(this, text, "串口扫描", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>Sidebar footer: where SSH keys / credentials live (v1 rules).</summary>
        private void ShowKeyManagerInfo()
        {
            string text =
                "SSH 私钥：在会话编辑弹窗中通过「私钥文件」指定（支持 PuTTY .ppk），" +
                "每个会话可单独配置。\r\n\r\n" +
                "密码：v1 不保存明文密码，连接时由 PuTTY 自行提示输入" +
                "（可用 Pageant 托管密钥）。";
            MessageBox.Show(this, text, "密钥与凭据", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private SessionDef SelectedSidebarSession()
        {
            if (Sidebar == null) return null;
            try
            {
                var prop = Sidebar.GetType().GetProperty("SelectedSession");
                if (prop != null) return prop.GetValue(Sidebar, null) as SessionDef;
            }
            catch { }
            return null;
        }

        private void ShowTabMoreMenu()
        {
            ContextMenuStrip menu = DarkMenu.Create();
            menu.Items.Add(DarkMenu.Item("新建标签", (s, e) => NewTab()));
            menu.Items.Add(DarkMenu.Item("关闭标签", (s, e) => CloseActivePane()));
            menu.Items.Add(DarkMenu.Item("关闭其他标签", (s, e) => CloseOtherTabs()));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(DarkMenu.Item("左右分屏", (s, e) => SplitActive(true)));
            menu.Items.Add(DarkMenu.Item("上下分屏", (s, e) => SplitActive(false)));
            menu.Items.Add(DarkMenu.Item("四窗格 2×2", (s, e) => ApplyLayoutPreset(LayoutPreset.Quad)));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(DarkMenu.Item("切换侧栏", (s, e) => ToggleSidebar()));
            menu.Items.Add(DarkMenu.Item("宏管理…", (s, e) => OpenMacroEditor()));
            menu.Items.Add(DarkMenu.Item("新建会话…", (s, e) => OpenSessionEditor(null)));
            menu.Items.Add(DarkMenu.Item("设置…", (s, e) => OpenSettings()));
            menu.Show(TabBar, new Point(Math.Max(0, TabBar.Width - Theme.S(200)),
                                        TabBar.Height + Theme.S(2)));
        }

        private void CloseOtherTabs()
        {
            var keep = ActiveTab;
            for (int i = _tabs.Count - 1; i >= 0; i--)
            {
                if (_tabs[i] == keep) continue;
                CloseTab(i);
            }
        }

        // ==================================================================
        // Startup arguments
        // ==================================================================

        private void ApplyStartupArguments()
        {
            string[] args = StartupArguments;
            if (args == null || args.Length == 0) return;

            var sessions = new List<SessionDef>();
            bool split = false;
            string layout = null;

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i] ?? "";
                if (a == "--session" && i + 1 < args.Length)
                {
                    AddIfFound(sessions, FindSession(args[++i]));
                }
                else if (a.StartsWith("--session=", StringComparison.Ordinal))
                {
                    AddIfFound(sessions, FindSession(a.Substring(10)));
                }
                else if (a == "--raw" && i + 1 < args.Length)
                {
                    int port;
                    if (int.TryParse(args[++i], out port)) sessions.Add(MakeRawSession(port));
                }
                else if (a.StartsWith("--raw=", StringComparison.Ordinal))
                {
                    int port;
                    if (int.TryParse(a.Substring(6), out port)) sessions.Add(MakeRawSession(port));
                }
                else if (a == "--split")
                {
                    split = true;
                }
                else if (a == "--layout" && i + 1 < args.Length)
                {
                    layout = args[++i];
                }
                else if (a.StartsWith("--layout=", StringComparison.Ordinal))
                {
                    layout = a.Substring(9);
                }
                else if (a == "--theme" && i + 1 < args.Length)
                {
                    ApplyThemeArg(args[++i]);
                }
                else if (a.StartsWith("--theme=", StringComparison.Ordinal))
                {
                    ApplyThemeArg(a.Substring(8));
                }
            }

            if (sessions.Count == 0) return;
            Diag.Log("ApplyStartupArguments sessions=" + sessions.Count + " split=" + split +
                     " layout=" + (layout ?? ""));

            if (!split)
            {
                for (int i = 0; i < sessions.Count; i++) OpenSessionInTab(sessions[i], true);
            }
            else
            {
                OpenSessionsInSplit(sessions);
            }

            if (!string.IsNullOrEmpty(layout))
                ApplyLayoutPreset(ParsePreset(layout));
        }

        private static void AddIfFound(List<SessionDef> list, SessionDef s)
        {
            if (s != null) list.Add(s);
        }

        /// <summary>Apply a colour scheme from --theme (also used by tests).</summary>
        private void ApplyThemeArg(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            bool light = string.Equals(name, "light", StringComparison.OrdinalIgnoreCase);
            AppContext.I.Settings.Theme = light ? "light" : "dark";
            Theme.SetScheme(AppContext.I.Settings.Theme);
            AppContext.I.BuildThemeIni();
            ApplyDwmEffects();
        }

        private SessionDef FindSession(string nameOrId)
        {
            if (string.IsNullOrEmpty(nameOrId)) return null;
            for (int i = 0; i < AppContext.I.Sessions.Count; i++)
            {
                SessionDef s = AppContext.I.Sessions[i];
                if (s.Id == nameOrId || s.Name == nameOrId) return s;
            }
            for (int i = 0; i < AppContext.I.Sessions.Count; i++)
            {
                SessionDef s = AppContext.I.Sessions[i];
                if (s.Name != null && s.Name.IndexOf(nameOrId, StringComparison.OrdinalIgnoreCase) >= 0)
                    return s;
            }
            return null;
        }

        private static SessionDef MakeRawSession(int port)
        {
            var s = new SessionDef();
            s.Name = "本地测试:" + port;
            s.Group = "本地串口";
            s.Protocol = ProtocolKind.Raw;
            s.Host = "127.0.0.1";
            s.Port = port;
            s.ColorTag = "teal";
            return s;
        }

        private void OpenSessionsInSplit(List<SessionDef> sessions)
        {
            if (sessions.Count == 0) return;
            OpenSessionInTab(sessions[0], true);
            for (int i = 1; i < sessions.Count; i++)
            {
                TermTab tab = ActiveTab;
                if (tab == null) return;
                PaneView target = tab.Host != null ? tab.Host.ActivePane : null;
                if (target == null) break;
                bool vertical = (i % 2) == 1;
                AddPaneToTab(tab, target, vertical, sessions[i].Clone());
            }
        }

        private static LayoutPreset ParsePreset(string text)
        {
            switch ((text ?? "").ToLowerInvariant())
            {
                case "v2":
                case "2columns":
                case "columns": return LayoutPreset.TwoColumns;
                case "h2":
                case "2rows":
                case "rows": return LayoutPreset.TwoRows;
                case "v3":
                case "3columns": return LayoutPreset.ThreeColumns;
                case "h3":
                case "3rows": return LayoutPreset.ThreeRows;
                case "quad":
                case "2x2": return LayoutPreset.Quad;
                default: return LayoutPreset.Single;
            }
        }

        // ==================================================================
        // putty.exe notifications
        // ==================================================================

        protected override void WndProc(ref Message m)
        {
            if ((uint)m.Msg == (uint)PuttyMessages.MsgCreated)
            {
                OnPuttyCreated((IntPtr)m.WParam);
                return;
            }
            if ((uint)m.Msg == (uint)PuttyMessages.MsgSessionExit)
            {
                Diag.Log("xPuTTYSessionExit code=" + m.WParam.ToInt64());
                OnSessionExitNotify((int)m.WParam.ToInt64());
                return;
            }
            if ((uint)m.Msg == (uint)PuttyMessages.MsgHotkey)
            {
                HandleShellHotkey((int)m.WParam.ToInt64(), m.LParam != IntPtr.Zero);
                return;
            }
            if ((uint)m.Msg == WM_COPYDATA)
            {
                // Fatal error text from the fork (magic 'XPTF'): the shell
                // renders it inside the pane.  WM_COPYDATA is the only safe way
                // to pass a string across process boundaries.
                var cds = (PuttyMessages.COPYDATASTRUCT)Marshal.PtrToStructure(
                    m.LParam, typeof(PuttyMessages.COPYDATASTRUCT));
                if ((uint)cds.dwData.ToInt64() == XPUTTY_FATAL_MAGIC && cds.cbData > 0)
                {
                    string text = null;
                    try
                    {
                        var bytes = new byte[cds.cbData];
                        Marshal.Copy(cds.lpData, bytes, 0, cds.cbData);
                        int len = Array.IndexOf(bytes, (byte)0);
                        if (len < 0) len = bytes.Length;
                        text = Encoding.UTF8.GetString(bytes, 0, len);
                    }
                    catch { }
                    if (!IsPlausibleFatalText(text)) text = ReadRemoteFatalText(cds.lpData);
                    if (!string.IsNullOrEmpty(text))
                        _pendingFatals.Add(new FatalNotice { Text = text, Time = DateTime.Now });
                    Diag.Log("xPuTTYFatal(COPYDATA) text='" + (text ?? "(null)") + "'");
                    m.Result = (IntPtr)1;
                    return;
                }
            }
            if ((uint)m.Msg == (uint)PuttyMessages.MsgFatal)
            {
                // Legacy path (pre-WM_COPYDATA fork builds): the text arrives as
                // a raw pointer that is only valid in the sender's process.
                string text = null;
                try { text = Marshal.PtrToStringAnsi(m.LParam); }
                catch { }
                if (!IsPlausibleFatalText(text))
                    text = ReadRemoteFatalText(m.LParam);
                if (!string.IsNullOrEmpty(text))
                    _pendingFatals.Add(new FatalNotice { Text = text, Time = DateTime.Now });
                Diag.Log("xPuTTYFatal lParam=" + m.LParam.ToInt64() +
                         " text='" + (text ?? "(null)") + "'");
                m.Result = IntPtr.Zero;
                return;
            }

            switch (m.Msg)
            {
                case WM_NCCALCSIZE:
                    if (m.WParam != IntPtr.Zero)
                    {
                        if (WindowState == FormWindowState.Maximized)
                        {
                            var rc = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                            Rectangle work = Screen.FromHandle(Handle).WorkingArea;
                            rc.Left = work.Left;
                            rc.Top = work.Top;
                            rc.Right = work.Right;
                            rc.Bottom = work.Bottom;
                            Marshal.StructureToPtr(rc, m.LParam, false);
                        }
                        m.Result = IntPtr.Zero;
                        return;
                    }
                    break;

                case WM_NCHITTEST:
                    {
                        long lp = m.LParam.ToInt64();
                        int sx = (short)(lp & 0xFFFF);
                        int sy = (short)((lp >> 16) & 0xFFFF);
                        m.Result = (IntPtr)HitTestNc(new Point(sx, sy));
                        return;
                    }

                case WM_GETMINMAXINFO:
                    {
                        var mmi = (MINMAXINFO)Marshal.PtrToStructure(m.LParam, typeof(MINMAXINFO));
                        Rectangle work = Screen.FromHandle(Handle).WorkingArea;
                        int minW = Math.Min(Theme.S(1100), (int)(work.Width * 0.92f));
                        int minH = Math.Min(Theme.S(700), (int)(work.Height * 0.92f));
                        mmi.ptMinTrackSize.X = Math.Max(Theme.S(320), minW);
                        mmi.ptMinTrackSize.Y = Math.Max(Theme.S(240), minH);
                        Marshal.StructureToPtr(mmi, m.LParam, false);
                        m.Result = IntPtr.Zero;
                        return;
                    }

                case WM_NCMOUSEMOVE:
                    UpdateHotTitle(Cursor.Position);
                    break;

                case WM_NCMOUSELEAVE:
                case WM_MOUSELEAVE:
                    if (_hotTitle != 0) { _hotTitle = 0; InvalidateTitleBar(); }
                    break;

                case WM_NCACTIVATE:
                    InvalidateTitleBar();
                    break;

                case WM_DPICHANGED:
                    {
                        int dpi = (int)m.WParam.ToInt64() & 0xFFFF;
                        var rc = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                        Diag.Log("WM_DPICHANGED dpi=" + dpi + " seen=" + _dpiChangeSeen +
                                 " suggested=(" + rc.Left + "," + rc.Top + "," +
                                 (rc.Right - rc.Left) + "x" + (rc.Bottom - rc.Top) +
                                 ") current=" + Width + "x" + Height);
                        Theme.Scale = dpi / 96f;
                        UiFonts.Reset();
                        if (_dpiChangeSeen)
                        {
                            // Only follow the suggested rectangle for genuine
                            // monitor moves, and only when it actually fits the
                            // target monitor.  The first notifications arrive
                            // right after creation, when .NET Framework's
                            // per-monitor-v2 startup suggests doubled bounds.
                            Rectangle target = new Rectangle(rc.Left, rc.Top,
                                                             rc.Right - rc.Left, rc.Bottom - rc.Top);
                            Rectangle work = Screen.FromHandle(Handle).WorkingArea;
                            if (target.Width <= work.Width && target.Height <= work.Height)
                                Bounds = target;
                        }
                        _dpiChangeSeen = true;
                        PerformLayoutCore();
                        Invalidate(true);
                        m.Result = IntPtr.Zero;
                        return;
                    }

                case WM_ERASEBKGND:
                    m.Result = (IntPtr)1;
                    return;
            }

            base.WndProc(ref m);
        }

        private int HitTestNc(Point screen)
        {
            Point p = PointToClient(screen);
            if (p.X < 0 || p.Y < 0 || p.X >= Width || p.Y >= Height) return 0; // HTNOWHERE

            bool maximized = WindowState == FormWindowState.Maximized;
            if (!maximized)
            {
                int b = Theme.S(6);
                bool left = p.X < b;
                bool right = p.X >= Width - b;
                bool top = p.Y < b;
                bool bottom = p.Y >= Height - b;
                if (top && left) return HTTOPLEFT;
                if (top && right) return HTTOPRIGHT;
                if (bottom && left) return HTBOTTOMLEFT;
                if (bottom && right) return HTBOTTOMRIGHT;
                if (left) return HTLEFT;
                if (right) return HTRIGHT;
                if (top) return HTTOP;
                if (bottom) return HTBOTTOM;
            }

            if (p.Y < Theme.S(TitleBarHeight))
            {
                if (_btnClose.Contains(p)) return HTCLOSE;
                if (_btnMax.Contains(p)) return HTMAXBUTTON;
                if (_btnMin.Contains(p)) return HTMINBUTTON;
                if (_btnSettings.Contains(p) || _btnCommand.Contains(p)) return HTCLIENT;
                return HTCAPTION;
            }
            return HTCLIENT;
        }

        private void UpdateHotTitle(Point screen)
        {
            Point p = PointToClient(screen);
            int hot = 0;
            if (p.X >= 0 && p.Y >= 0 && p.X < Width && p.Y < Theme.S(TitleBarHeight))
            {
                if (_btnClose.Contains(p)) hot = 3;
                else if (_btnMax.Contains(p)) hot = 2;
                else if (_btnMin.Contains(p)) hot = 1;
                else if (_btnSettings.Contains(p)) hot = 4;
                else if (_btnCommand.Contains(p)) hot = 5;
            }
            if (hot != _hotTitle)
            {
                _hotTitle = hot;
                InvalidateTitleBar();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            UpdateHotTitle(PointToScreen(e.Location));
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hotTitle != 0)
            {
                _hotTitle = 0;
                InvalidateTitleBar();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (e.Y >= Theme.S(TitleBarHeight)) return;
            if (_btnClose.Contains(e.Location))
            {
                Close();
                return;
            }
            if (_btnMax.Contains(e.Location))
            {
                WindowState = WindowState == FormWindowState.Maximized
                    ? FormWindowState.Normal : FormWindowState.Maximized;
                return;
            }
            if (_btnMin.Contains(e.Location))
            {
                WindowState = FormWindowState.Minimized;
                return;
            }
            if (_btnSettings.Contains(e.Location))
            {
                OpenSettings();
                return;
            }
            if (_btnCommand.Contains(e.Location))
            {
                CommandPalette();
                return;
            }
        }

        private void CommandPalette()
        {
            if (Sidebar != null)
            {
                if (!_sidebarVisible)
                {
                    _sidebarVisible = true;
                    if (AppContext.I.Settings != null) AppContext.I.Settings.ShowSidebar = true;
                    PerformLayoutCore();
                }
                try
                {
                    var method = Sidebar.GetType().GetMethod("FocusSearch", Type.EmptyTypes);
                    if (method != null) method.Invoke(Sidebar, null);
                }
                catch { }
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            bool ctrl = (keyData & Keys.Control) == Keys.Control;
            bool shift = (keyData & Keys.Shift) == Keys.Shift;

            if (key == Keys.F11)
            {
                MaximizeActivePane();
                return true;
            }
            if (ctrl && shift)
            {
                switch (key)
                {
                    case Keys.T: NewTab(); return true;
                    case Keys.W: CloseActivePane(); return true;
                    case Keys.E: SplitActive(true); return true;
                    case Keys.D: SplitActive(false); return true;
                    case Keys.M: OpenMacroEditor(); return true;
                    case Keys.N: OpenSessionEditor(null); return true;
                    case Keys.Tab: NextTab(false); return true;
                    case Keys.D1: TriggerMacro(0); return true;
                    case Keys.D2: TriggerMacro(1); return true;
                    case Keys.D3: TriggerMacro(2); return true;
                    case Keys.D4: TriggerMacro(3); return true;
                    case Keys.D5: TriggerMacro(4); return true;
                    case Keys.D6: TriggerMacro(5); return true;
                    case Keys.D7: TriggerMacro(6); return true;
                    case Keys.D8: TriggerMacro(7); return true;
                    case Keys.D9: TriggerMacro(8); return true;
                }
            }
            if (ctrl && !shift)
            {
                switch (key)
                {
                    case Keys.Tab: NextTab(true); return true;
                    case Keys.N: OpenSessionEditor(null); return true;
                    case Keys.K: CommandPalette(); return true;
                    case Keys.Oemcomma: OpenSettings(); return true;
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ==================================================================
        // putty.exe message routing
        // ==================================================================

        private void OnPuttyCreated(IntPtr termHwnd)
        {
            if (termHwnd == IntPtr.Zero) return;
            uint pid;
            GetWindowThreadProcessId(termHwnd, out pid);
            PaneView pane = FindPaneByPid(pid) ?? FindUnboundPane();
            Diag.Log("OnPuttyCreated hwnd=" + termHwnd.ToInt64() + " pid=" + pid +
                     " pane=" + (pane != null ? "found" : "NOT FOUND"));
            if (pane != null) pane.AttachTerminal(termHwnd);
        }

        private PaneView FindPaneByPid(uint pid)
        {
            for (int i = 0; i < _tabs.Count; i++)
            {
                var panes = _tabs[i].Tree != null ? _tabs[i].Tree.EnumeratePanes() : new List<PaneView>();
                for (int j = 0; j < panes.Count; j++)
                {
                    PuttyProcess pp = panes[j].Putty;
                    if (pp != null && pp.Pid != 0 && (uint)pp.Pid == pid) return panes[j];
                }
            }
            return null;
        }

        private PaneView FindUnboundPane()
        {
            for (int i = _tabs.Count - 1; i >= 0; i--)
            {
                var panes = _tabs[i].Tree != null ? _tabs[i].Tree.EnumeratePanes() : new List<PaneView>();
                for (int j = panes.Count - 1; j >= 0; j--)
                {
                    PaneView p = panes[j];
                    if (p.Putty != null && !p.Putty.Attached) return p;
                }
            }
            return null;
        }

        private void OnSessionExitNotify(int exitCode)
        {
            _pendingExitCodes.Add(exitCode);
            if (_exitTimer == null)
            {
                _exitTimer = new Timer();
                _exitTimer.Interval = 200;
                _exitTimer.Tick += delegate { ResolvePendingExits(); };
                _exitTimerTicks = 0;
                _exitTimer.Start();
            }
        }

        private void ResolvePendingExits()
        {
            _exitTimerTicks++;
            if (_pendingExitCodes.Count == 0)
            {
                StopExitTimer();
                return;
            }

            // PuTTY retitles the window with " (inactive)" after close_session,
            // which lets us match the anonymous WM_APP notification to a pane.
            var candidates = new List<PaneView>();
            for (int i = 0; i < _tabs.Count; i++)
            {
                var panes = _tabs[i].Tree != null ? _tabs[i].Tree.EnumeratePanes() : new List<PaneView>();
                for (int j = 0; j < panes.Count; j++)
                {
                    PaneView p = panes[j];
                    if (!p.Connected) continue;
                    if (p.PuttyHwnd != IntPtr.Zero && PaneView.TitleIsInactive(p.PuttyHwnd))
                        candidates.Add(p);
                }
            }

            int assign = Math.Min(candidates.Count, _pendingExitCodes.Count);
            for (int i = 0; i < assign; i++)
            {
                // A fatal error text (network abort, connection refused, ...)
                // arrives synchronously just before the exit notification, so
                // prefer it over the generic "connection closed" wording.
                string fatal = TakePendingFatal();
                if (!string.IsNullOrEmpty(fatal))
                    candidates[i].SetDisconnected(fatal, true);
                else
                    candidates[i].SetDisconnected(
                        "连接已断开（退出码 " + _pendingExitCodes[i] + "）", false);
            }
            if (assign > 0) _pendingExitCodes.RemoveRange(0, assign);

            if (_pendingExitCodes.Count == 0 || _exitTimerTicks > 15)
                StopExitTimer();
        }

        private void StopExitTimer()
        {
            if (_exitTimer == null) return;
            _exitTimer.Stop();
            _exitTimer.Dispose();
            _exitTimer = null;
            _exitTimerTicks = 0;
            _pendingExitCodes.Clear();
        }

        /// <summary>Consume a "fatal error" text that belongs to the process
        /// which just exited (putty sends it synchronously right before exit).</summary>
        public string TakePendingFatal()
        {
            for (int i = _pendingFatals.Count - 1; i >= 0; i--)
            {
                if ((DateTime.Now - _pendingFatals[i].Time).TotalSeconds <= 6)
                {
                    string text = _pendingFatals[i].Text;
                    _pendingFatals.RemoveAt(i);
                    return text;
                }
            }
            return null;
        }

        private static bool IsPlausibleFatalText(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length < 4) return false;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c < 32 && c != '\r' && c != '\n' && c != '\t') return false;
                if (c == '\ufffd') return false;
            }
            return true;
        }

        /// <summary>
        /// Read a string that lives in one of our putty child processes.  The
        /// fork sends xPuTTYFatal's text as a raw pointer (only valid in the
        /// sender), so the shell has to pull it out with ReadProcessMemory.
        /// </summary>
        private string ReadRemoteFatalText(IntPtr pointer)
        {
            if (pointer == IntPtr.Zero) return null;
            const uint PROCESS_VM_READ = 0x0010;
            const uint PROCESS_QUERY_INFORMATION = 0x0400;
            const int MaxBytes = 512;
            var buffer = new byte[MaxBytes];

            for (int i = 0; i < _tabs.Count; i++)
            {
                var panes = _tabs[i].Tree != null ? _tabs[i].Tree.EnumeratePanes() : new List<PaneView>();
                for (int j = 0; j < panes.Count; j++)
                {
                    PuttyProcess pp = panes[j].Putty;
                    if (pp == null) continue;
                    int pid = pp.Pid;
                    if (pid <= 0 || !pp.IsAlive) continue;
                    IntPtr handle = OpenProcess(PROCESS_VM_READ | PROCESS_QUERY_INFORMATION,
                                                false, (uint)pid);
                    if (handle == IntPtr.Zero) continue;
                    try
                    {
                        IntPtr read;
                        bool ok = ReadProcessMemory(handle, pointer, buffer, MaxBytes, out read);
                        if (!ok) ok = ReadProcessMemory(handle, pointer, buffer, 128, out read);
                        if (!ok) continue;
                        int len = 0;
                        while (len < MaxBytes && buffer[len] != 0) len++;
                        if (len < 4) continue;
                        string text = Encoding.Default.GetString(buffer, 0, len);
                        if (IsPlausibleFatalText(text)) return text;
                    }
                    catch { }
                    finally { CloseHandle(handle); }
                }
            }
            return null;
        }

        // ==================================================================
        // Win32
        // ==================================================================

        private struct POINT
        {
            public int X;
            public int Y;
        }

        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForSystem();

        [DllImport("user32.dll")]
        private static extern bool GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadProcessMemory(IntPtr process, IntPtr address,
                                                     byte[] buffer, int size, out IntPtr read);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute,
                                                        ref int value, int size);
    }
}
