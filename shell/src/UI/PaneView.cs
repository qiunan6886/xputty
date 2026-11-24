using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace XPutty
{
    /// <summary>
    /// A single terminal pane: 26px self-drawn pane header, an embedded
    /// putty.exe child window and a disconnect/reconnect overlay.
    /// </summary>
    public class PaneView : Control, Theme.IThemeAware
    {
        private const int WM_PARENTNOTIFY = 0x0210;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_MBUTTONDOWN = 0x0207;

        private const int SW_HIDE = 0;
        private const int SW_SHOW = 5;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;

        // --- public contract (SHELL_SPEC §8) --------------------------------
        public Panel EmbedPanel;
        public IntPtr PuttyHwnd = IntPtr.Zero;
        public SessionDef Session;
        public bool Connected;

        // --- extra state used by the shell ----------------------------------
        public PuttyProcess Putty;
        public SplitHost Host;
        public bool IsActive;
        public bool HasError;
        public bool Closing;
        public string DisconnectReason = "";

        public event Action<PaneView> CloseRequested;
        public event Action<PaneView> MaximizeRequested;
        public event Action<PaneView> Activated;
        public event Action<PaneView> StateChanged;

        private readonly OverlayPanel _overlay;
        private Timer _reconnectTimer;
        private int _stateGeneration;
        private bool _closed;
        private bool _startRequested;

        private const int HotNone = 0;
        private const int HotMax = 1;
        private const int HotClose = 2;
        private int _hot;

        public PaneView(SessionDef session)
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            BackColor = Theme.TermBg;
            DoubleBuffered = true;
            Session = session;

            var embed = new EmbedHostPanel();
            embed.Owner = this;
            embed.BackColor = Theme.TermBg;
            embed.Visible = session != null;
            EmbedPanel = embed;
            Controls.Add(embed);

            _overlay = new OverlayPanel();
            _overlay.Owner = this;
            _overlay.Visible = false;
            Controls.Add(_overlay);

            LayoutChildren();
        }

        // ------------------------------------------------------------------
        // Geometry
        // ------------------------------------------------------------------

        public int TitleHeight { get { return Theme.S(26); } }

        public static int InsetFor(bool hosted)
        {
            return Theme.S(hosted ? 3 : 1);
        }

        private int Inset { get { return InsetFor(Host != null); } }

        public Rectangle EmbedClientSize()
        {
            if (EmbedPanel == null) return Rectangle.Empty;
            return EmbedPanel.ClientRectangle;
        }

        private Rectangle BodyRect()
        {
            int inset = Inset;
            return new Rectangle(inset, TitleHeight,
                                 Math.Max(0, Width - inset * 2),
                                 Math.Max(0, Height - TitleHeight - inset));
        }

        private void LayoutChildren()
        {
            Rectangle body = BodyRect();
            if (EmbedPanel != null && EmbedPanel.Bounds != body)
                EmbedPanel.SetBounds(body.X, body.Y, body.Width, body.Height);
            if (_overlay != null && _overlay.Bounds != body)
                _overlay.SetBounds(body.X, body.Y, body.Width, body.Height);
            if (PuttyHwnd != IntPtr.Zero) SizeTerminal();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutChildren();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            LayoutChildren();
            ScheduleEnsureStarted();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible) return;
            LayoutChildren();
            ScheduleEnsureStarted();
        }

        private void ScheduleEnsureStarted()
        {
            if (Session == null || Putty != null) return;
            if (!_startRequested)
            {
                _startRequested = true;
                BeginInvokeSafe(new Action(EnsureStarted));
            }
        }

        private void SizeTerminal()
        {
            if (PuttyHwnd == IntPtr.Zero || EmbedPanel == null) return;
            if (!IsWindow(PuttyHwnd)) { PuttyHwnd = IntPtr.Zero; return; }
            int w = Math.Max(0, EmbedPanel.ClientSize.Width);
            int h = Math.Max(0, EmbedPanel.ClientSize.Height);
            SetWindowPos(PuttyHwnd, IntPtr.Zero, 0, 0, w, h,
                         SWP_NOZORDER | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        // ------------------------------------------------------------------
        // Session lifecycle
        // ------------------------------------------------------------------

        public void SetSession(SessionDef session)
        {
            Session = session;
            HasError = false;
            DisconnectReason = "";
            Connected = false;
            PuttyHwnd = IntPtr.Zero;
            if (Putty != null) { Putty.Release(); Putty = null; }
            if (EmbedPanel != null) EmbedPanel.Visible = session != null;
            if (_overlay != null) _overlay.Visible = false;
            _startRequested = false;
            LayoutChildren();
            Invalidate();
            RaiseState();
            if (Visible && session != null)
                BeginInvokeSafe(new Action(EnsureStarted));
        }

        /// <summary>Start the embedded putty.exe once the pane is really shown.</summary>
        public void EnsureStarted()
        {
            if (_closed || IsDisposed) { Diag.Log("EnsureStarted skip closed"); return; }
            if (Session == null) { Diag.Log("EnsureStarted skip no-session"); return; }
            if (Putty != null) return;
            if (!IsHandleCreated || !Visible)
            {
                Diag.Log("EnsureStarted skip not-visible handleCreated=" + IsHandleCreated +
                         " visible=" + Visible);
                return;
            }
            if (EmbedPanel == null) return;
            IntPtr paneHandle = EmbedPanel.Handle;
            if (paneHandle == IntPtr.Zero) { Diag.Log("EnsureStarted skip no-pane-handle"); return; }
            MainForm main = AppContext.I.Main;
            IntPtr hostHandle = main != null && main.IsHandleCreated ? main.Handle : IntPtr.Zero;
            if (hostHandle == IntPtr.Zero) { Diag.Log("EnsureStarted skip no-host-handle"); return; }

            _startRequested = true;
            Putty = new PuttyProcess(this, Session, paneHandle, hostHandle);
            Diag.Log("EnsureStarted launching session=" + Session.Name +
                     " paneHwnd=" + paneHandle.ToInt64() + " hostHwnd=" + hostHandle.ToInt64());
            if (!Putty.Start())
            {
                HasError = true;
                SetDisconnected(Putty.LastError ?? "无法启动 putty.exe", true);
            }
            else
            {
                HasError = false;
                DisconnectReason = "";
                if (_overlay != null) _overlay.Visible = false;
                if (EmbedPanel != null) EmbedPanel.Visible = true;
                Invalidate();
                RaiseState();
            }
        }

        /// <summary>Routed from MainForm when the putty.exe window was created.</summary>
        public void AttachTerminal(IntPtr hwnd)
        {
            _stateGeneration++;
            PuttyHwnd = hwnd;
            if (Putty != null) Putty.AttachWindow(hwnd);
            HasError = false;
            DisconnectReason = "";
            Connected = true;
            StopReconnectWatch();
            if (EmbedPanel != null) EmbedPanel.Visible = true;
            if (_overlay != null) _overlay.Visible = false;
            LayoutChildren();
            SizeTerminal();
            if (IsActive) FocusTerminal();
            Invalidate();
            RaiseState();
        }

        public void SetDisconnected(string reason)
        {
            SetDisconnected(reason, false);
        }

        public void SetDisconnected(string reason, bool isError)
        {
            if (_closed) return;
            _stateGeneration++;
            HasError = isError;
            DisconnectReason = reason ?? "";
            Connected = false;
            StopReconnectWatch();
            if (PuttyHwnd != IntPtr.Zero && IsWindow(PuttyHwnd))
                ShowWindow(PuttyHwnd, SW_HIDE);
            if (EmbedPanel != null) EmbedPanel.Visible = false;
            if (_overlay != null)
            {
                _overlay.SetState(DisconnectReason, isError);
                _overlay.Visible = true;
                _overlay.BringToFront();
                _overlay.Invalidate();
            }
            Invalidate();
            RaiseState();
        }

        /// <summary>Called by the owning PuttyProcess when the child exits.</summary>
        public void OnPuttyProcessExited(PuttyProcess process)
        {
            if (_closed || IsDisposed) return;
            if (process != Putty) return;
            PuttyHwnd = IntPtr.Zero;
            Connected = false;
            MainForm main = AppContext.I.Main;
            string fatal = main != null ? main.TakePendingFatal() : null;
            if (!string.IsNullOrEmpty(fatal))
            {
                SetDisconnected(fatal, true);
                return;
            }
            int code = process.ExitCode;
            SetDisconnected(code != int.MinValue
                ? "连接已关闭（退出码 " + code + "）"
                : "连接已关闭", false);
        }

        /// <summary>Reconnect: restart in the same terminal window if it still
        /// exists, otherwise spawn a fresh putty.exe.</summary>
        public void Reconnect()
        {
            if (_closed || Session == null) return;
            if (Putty != null && Putty.IsAlive && Putty.Attached && IsWindow(Putty.PuttyHwnd))
            {
                if (_overlay != null) _overlay.Visible = false;
                if (EmbedPanel != null) EmbedPanel.Visible = true;
                HasError = false;
                DisconnectReason = "";
                LayoutChildren();
                Putty.Restart();
                Invalidate();
                RaiseState();
                StartReconnectWatch(true);
            }
            else
            {
                if (Putty != null) { Putty.Release(); Putty = null; }
                PuttyHwnd = IntPtr.Zero;
                Connected = false;
                HasError = false;
                DisconnectReason = "";
                if (_overlay != null) _overlay.Visible = false;
                if (EmbedPanel != null) EmbedPanel.Visible = true;
                LayoutChildren();
                Invalidate();
                RaiseState();
                _startRequested = false;
                EnsureStarted();
                StartReconnectWatch(false);
            }
        }

        private void StartReconnectWatch(bool sameProcess)
        {
            StopReconnectWatch();
            _reconnectTimer = new Timer();
            _reconnectTimer.Interval = 500;
            int ticks = 0;
            int generation = _stateGeneration;
            _reconnectTimer.Tick += delegate
            {
                ticks++;
                if (_closed || IsDisposed) { StopReconnectWatch(); return; }
                if (Connected || generation != _stateGeneration) { StopReconnectWatch(); return; }
                if (!sameProcess && PuttyHwnd != IntPtr.Zero)
                {
                    MarkConnected();
                    return;
                }
                if (sameProcess && PuttyHwnd != IntPtr.Zero)
                {
                    // PuTTY retitles the window with the host name once the
                    // session is up again; "(inactive)" means it is still down.
                    if (!TitleIsInactive(PuttyHwnd) || ticks >= 3)
                    {
                        MarkConnected();
                        return;
                    }
                }
                if (ticks >= 16)
                {
                    StopReconnectWatch();
                    SetDisconnected("重连超时，请检查网络或设备", true);
                }
            };
            _reconnectTimer.Start();
        }

        private void MarkConnected()
        {
            StopReconnectWatch();
            Connected = true;
            HasError = false;
            DisconnectReason = "";
            if (_overlay != null) _overlay.Visible = false;
            if (EmbedPanel != null) EmbedPanel.Visible = true;
            LayoutChildren();
            Invalidate();
            RaiseState();
        }

        private void StopReconnectWatch()
        {
            if (_reconnectTimer == null) return;
            _reconnectTimer.Stop();
            _reconnectTimer.Dispose();
            _reconnectTimer = null;
        }

        public void FocusTerminal()
        {
            if (PuttyHwnd != IntPtr.Zero && IsWindow(PuttyHwnd))
                PuttyMessages.FocusChild(PuttyHwnd);
        }

        public void Activate()
        {
            if (Host != null) Host.SetActivePane(this, true);
            if (Activated != null) Activated(this);
        }

        public bool InjectText(string text, bool appendEnter)
        {
            if (Putty == null) return false;
            return Putty.InjectText(text, appendEnter);
        }

        /// <summary>Close this pane; the owner disposes the control.</summary>
        public void ApplyThemeColors()
        {
            BackColor = Theme.TermBg;
            if (EmbedPanel != null) EmbedPanel.BackColor = Theme.TermBg;
            Invalidate(true);
        }

        public void Close()
        {
            if (_closed) return;
            _closed = true;
            Closing = true;
            StopReconnectWatch();
            if (_overlay != null) _overlay.Visible = false;
            if (Putty != null) Putty.Close();
        }

        public bool IsClosed { get { return _closed; } }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                StopReconnectWatch();
                if (Putty != null)
                {
                    Putty.Kill();
                    Putty.Release();
                    Putty = null;
                }
            }
            base.Dispose(disposing);
        }

        private void RaiseState()
        {
            if (StateChanged != null) StateChanged(this);
        }

        private void BeginInvokeSafe(Action action)
        {
            try
            {
                if (IsHandleCreated && !IsDisposed) BeginInvoke(action);
            }
            catch { }
        }

        // ------------------------------------------------------------------
        // Painting
        // ------------------------------------------------------------------

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.TermBg);

            DrawTitleBar(g);

            if (Session == null)
            {
                DrawEmptyHint(g);
            }

            if (IsActive)
            {
                using (var pen = new Pen(Color.FromArgb(110, Theme.Accent)))
                    g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
            }

            if (!_startRequested && Session != null && Putty == null && Visible && IsHandleCreated)
            {
                _startRequested = true;
                BeginInvokeSafe(new Action(EnsureStarted));
            }
        }

        private void DrawEmptyHint(Graphics g)
        {
            Rectangle body = BodyRect();
            using (var font = UiFonts.UI(12.5f, FontStyle.Regular))
            using (var small = UiFonts.UI(11f, FontStyle.Regular))
            {
                const string title = "未打开会话";
                const string hint = "在左侧双击会话，或按 Ctrl+Shift+T 新建标签";
                int tw = TextRenderer.MeasureText(title, font).Width;
                int th = TextRenderer.MeasureText(title, font).Height;
                int hw = TextRenderer.MeasureText(hint, small).Width;
                int hh = TextRenderer.MeasureText(hint, small).Height;
                int cx = body.X + body.Width / 2;
                int cy = body.Y + body.Height / 2;
                Theme.DrawText(g, title, font, Theme.TextSecondary,
                    new Rectangle(cx - tw / 2, cy - th - Theme.S(2), tw, th),
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                Theme.DrawText(g, hint, small, Theme.TextTertiary,
                    new Rectangle(cx - hw / 2, cy + Theme.S(6), hw, hh),
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            }
        }

        private void DrawTitleBar(Graphics g)
        {
            int h = TitleHeight;
            var barRect = new Rectangle(0, 0, Width, h);
            using (var b = new SolidBrush(IsActive ? Theme.TermHeader : Theme.Bg1))
                g.FillRectangle(b, barRect);
            using (var pen = new Pen(Theme.BorderSoft))
                g.DrawLine(pen, 0, h - 1, Width, h - 1);

            int pad = Theme.S(10);
            int midY = h / 2;

            // status dot
            Color dot = Connected ? Theme.Success
                      : HasError ? Theme.Danger
                      : (Session != null && Putty != null && Putty.Started) ? Theme.Warning
                      : Theme.TextDisabled;
            int dotD = Theme.S(6);
            int dx = pad;
            if (Connected)
            {
                using (var halo = new SolidBrush(Color.FromArgb(32, dot)))
                    g.FillEllipse(halo, dx - Theme.S(3), midY - dotD / 2 - Theme.S(3),
                                  dotD + Theme.S(6), dotD + Theme.S(6));
            }
            using (var b = new SolidBrush(dot))
                g.FillEllipse(b, dx, midY - dotD / 2, dotD, dotD);

            // title buttons (right)
            var closeRect = CloseButtonRect();
            var maxRect = MaxButtonRect();

            int rightLimit = maxRect.Left - Theme.S(8);

            // protocol chip (right aligned, before the buttons)
            string chipText = ProtocolChipText(Session);
            Color chipColor = ProtocolChipColor(Session);
            int chipW = 0;
            if (!string.IsNullOrEmpty(chipText))
            {
                using (var chipFont = UiFonts.UI(10f, FontStyle.Regular))
                {
                    chipW = TextRenderer.MeasureText(chipText, chipFont).Width + Theme.S(12);
                    chipW = Math.Min(chipW, Theme.S(72));
                    var chip = new Rectangle(rightLimit - chipW, midY - Theme.S(9),
                                             chipW, Theme.S(18));
                    Theme.FillRound(g, chip, Theme.S(4), Color.FromArgb(34, chipColor));
                    Theme.DrawText(g, chipText, chipFont, chipColor, chip,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix |
                        TextFormatFlags.EndEllipsis);
                    rightLimit = chip.Left - Theme.S(8);
                }
            }

            // name + protocol
            string name = Session != null ? Session.Name : "未分配会话";
            using (var nameFont = UiFonts.UI(11.5f, FontStyle.Bold))
            using (var subFont = UiFonts.Mono(10f, FontStyle.Regular))
            {
                int nameW = TextRenderer.MeasureText(name, nameFont).Width;
                int maxName = Math.Max(0, rightLimit - (dx + dotD + Theme.S(7)) - Theme.S(40));
                nameW = Math.Min(nameW, maxName);
                var nameRect = new Rectangle(dx + dotD + Theme.S(7), 0, nameW, h);
                Theme.DrawText(g, name, nameFont,
                    IsActive ? Theme.TextPrimary : Theme.TextSecondary, nameRect,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left |
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix |
                    TextFormatFlags.EndEllipsis);

                int subX = nameRect.Right + Theme.S(6);
                int subW = rightLimit - subX;
                if (subW > Theme.S(24))
                {
                    string sub = ProtocolLine(Session);
                    Theme.DrawText(g, sub, subFont, Theme.TextTertiary,
                        new Rectangle(subX, 0, subW, h),
                        TextFormatFlags.VerticalCenter | TextFormatFlags.Left |
                        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix |
                        TextFormatFlags.EndEllipsis);
                }
            }

            // pane buttons
            DrawPaneButton(g, maxRect, HotMax, "max");
            DrawPaneButton(g, closeRect, HotClose, "close");
        }

        private Rectangle CloseButtonRect()
        {
            int w = Theme.S(20), h = Theme.S(18);
            return new Rectangle(Width - Theme.S(6) - w, (TitleHeight - h) / 2, w, h);
        }

        private Rectangle MaxButtonRect()
        {
            int w = Theme.S(20), h = Theme.S(18);
            var c = CloseButtonRect();
            return new Rectangle(c.Left - Theme.S(2) - w, (TitleHeight - h) / 2, w, h);
        }

        private void DrawPaneButton(Graphics g, Rectangle r, int hot, string icon)
        {
            bool hover = _hot == hot;
            bool isMax = hot == HotMax;
            bool maximized = HasMaximizedPane();
            if (hover)
                Theme.FillRound(g, r, Theme.S(4), hot == HotClose
                    ? Color.FromArgb(40, Theme.Danger) : Theme.Bg3);
            Color c = hot == HotClose
                ? (hover ? Theme.Danger : Theme.TextTertiary)
                : (hover ? Theme.TextPrimary : Theme.TextTertiary);
            string kind = isMax ? (maximized ? "restore" : "max") : "close";
            Theme.DrawIcon(g, kind, new Rectangle(r.X + Theme.S(3), r.Y + Theme.S(1),
                r.Width - Theme.S(6), r.Height - Theme.S(2)), c);
        }

        private bool HasMaximizedPane()
        {
            return Host != null && Host.MaximizedPane == this;
        }

        /// <summary>Header chip label driven by the session protocol.</summary>
        public static string ProtocolChipText(SessionDef s)
        {
            if (s == null) return "";
            switch (s.Protocol)
            {
                case ProtocolKind.Serial: return "本地串口";
                case ProtocolKind.Telnet: return "TELNET";
                case ProtocolKind.Raw: return "RAW";
                default: return "SSH";
            }
        }

        public static Color ProtocolChipColor(SessionDef s)
        {
            if (s == null) return Theme.TextTertiary;
            switch (s.Protocol)
            {
                case ProtocolKind.Serial: return Theme.Teal;
                case ProtocolKind.Telnet: return Theme.Purple;
                case ProtocolKind.Raw: return Theme.TextSecondary;
                default: return Theme.Info;
            }
        }

        public static string ProtocolLine(SessionDef s)
        {
            if (s == null) return "";
            if (s.Protocol == ProtocolKind.Serial)
                return "serial " + s.SerialPort + " · " + (s.SerialConfig ?? "").Replace(",", "-");
            string proto = s.Protocol == ProtocolKind.Telnet ? "telnet"
                         : s.Protocol == ProtocolKind.Raw ? "raw" : "ssh";
            string user = string.IsNullOrEmpty(s.User) ? "" : s.User + "@";
            return proto + " " + user + (s.Host ?? "") + (s.Port > 0 ? ":" + s.Port : "");
        }

        // ------------------------------------------------------------------
        // Mouse
        // ------------------------------------------------------------------

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Activate();
            if (e.Button != MouseButtons.Left) return;

            if (HasMaximizedPane() && Host != null)
            {
                // In maximized mode keep the header usable but ignore splitters.
            }
            else if (Host != null && Host.TryBeginSplitterDrag(PointToScreen(e.Location)))
            {
                return;
            }

            if (e.Y < TitleHeight)
            {
                var close = CloseButtonRect();
                var max = MaxButtonRect();
                if (close.Contains(e.Location))
                {
                    if (CloseRequested != null) CloseRequested(this);
                    return;
                }
                if (max.Contains(e.Location))
                {
                    if (MaximizeRequested != null) MaximizeRequested(this);
                    return;
                }
                FocusTerminal();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            int hot = HotNone;
            if (e.Y < TitleHeight)
            {
                if (CloseButtonRect().Contains(e.Location)) hot = HotClose;
                else if (MaxButtonRect().Contains(e.Location)) hot = HotMax;
            }
            if (hot != _hot) { _hot = hot; Invalidate(new Rectangle(0, 0, Width, TitleHeight)); }

            if (Host != null)
            {
                bool vertical;
                if (Host.IsSplitterAt(PointToScreen(e.Location), out vertical))
                {
                    Cursor = vertical ? Cursors.SizeWE : Cursors.SizeNS;
                    var d = Host.HitDivider(Host.PointToClient(PointToScreen(e.Location)));
                    Host.SetHoverDivider(d);
                    return;
                }
                Host.ClearHover();
            }
            Cursor = hot == HotNone ? Cursors.Default : Cursors.Hand;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_hot != HotNone)
            {
                _hot = HotNone;
                Invalidate(new Rectangle(0, 0, Width, TitleHeight));
            }
            if (Host != null) Host.ClearHover();
            Cursor = Cursors.Default;
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (Host == null) return;
            if (Host.HitDivider(e.Location) != null)
                Host.ResetDividerAt(PointToScreen(e.Location));
            else if (e.Y < TitleHeight && MaximizeRequested != null)
                MaximizeRequested(this);
        }

        /// <summary>Embedded putty window was clicked: activate the pane and
        /// give the terminal keyboard focus (PuTTY itself never calls
        /// SetFocus on a mouse click, the shell has to do it).</summary>
        internal void OnChildClicked()
        {
            Activate();
            FocusTerminal();
        }

        // ------------------------------------------------------------------
        // Child controls
        // ------------------------------------------------------------------

        /// <summary>Panel that hosts the embedded putty HWND; forwards
        /// WM_PARENTNOTIFY clicks so a terminal click activates the pane.</summary>
        private sealed class EmbedHostPanel : Panel
        {
            public PaneView Owner;

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_PARENTNOTIFY && Owner != null && !Owner._closed)
                {
                    int evt = (int)(m.WParam.ToInt64() & 0xFFFF);
                    if (evt == WM_LBUTTONDOWN || evt == WM_RBUTTONDOWN || evt == WM_MBUTTONDOWN)
                        Owner.OnChildClicked();
                }
                base.WndProc(ref m);
            }
        }

        /// <summary>Semi-transparent "disconnected" overlay with reconnect/close.</summary>
        private sealed class OverlayPanel : Control, Theme.IThemeAware
        {
            public PaneView Owner;
            private string _title = "连接已断开";
            private string _reason = "";
            private bool _error;
            private Rectangle _btnReconnect;
            private Rectangle _btnClose;
            private int _hot;

            public OverlayPanel()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.UserPaint |
                         ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.ResizeRedraw, true);
                BackColor = Theme.TermBg;
            }

            public void SetState(string reason, bool error)
            {
                _error = error;
                _title = error ? "连接失败" : "连接已断开";
                _reason = reason ?? "";
                Invalidate();
            }

            public void ApplyThemeColors()
            {
                BackColor = Theme.TermBg;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                // Nearly-opaque wash that hides the stale terminal content and
                // keeps the message readable in both colour schemes.
                Color scrim = Theme.IsLight
                    ? Color.FromArgb(216, 250, 251, 253)
                    : Color.FromArgb(216, 5, 7, 11);
                using (var b = new SolidBrush(scrim))
                    g.FillRectangle(b, ClientRectangle);

                int midX = Width / 2;
                int midY = Height / 2;
                int dot = Theme.S(8);
                Color dotColor = _error ? Theme.Danger : Theme.Warning;
                using (var halo = new SolidBrush(Color.FromArgb(36, dotColor)))
                    g.FillEllipse(halo, midX - dot / 2 - Theme.S(6), midY - Theme.S(52) - dot / 2 - Theme.S(6),
                                  dot + Theme.S(12), dot + Theme.S(12));
                using (var b = new SolidBrush(dotColor))
                    g.FillEllipse(b, midX - dot / 2, midY - Theme.S(52) - dot / 2, dot, dot);

                using (var font = UiFonts.UI(13.5f, FontStyle.Bold))
                using (var small = UiFonts.UI(11f, FontStyle.Regular))
                {
                    int tw = TextRenderer.MeasureText(_title, font).Width;
                    int th = TextRenderer.MeasureText(_title, font).Height;
                    Theme.DrawText(g, _title, font, Theme.TextPrimary,
                        new Rectangle(midX - tw / 2, midY - Theme.S(30), tw, th),
                        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

                    string reason = _reason;
                    if (string.IsNullOrEmpty(reason)) reason = "远端主机或设备已关闭连接";
                    int maxW = Math.Max(Theme.S(120), Width - Theme.S(40));
                    var reasonSize = TextRenderer.MeasureText(reason, small);
                    int rw = Math.Min(reasonSize.Width, maxW);
                    int rh = reasonSize.Height;
                    Theme.DrawText(g, reason, small, Theme.TextSecondary,
                        new Rectangle(midX - rw / 2, midY - Theme.S(8), rw, rh),
                        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix |
                        TextFormatFlags.EndEllipsis);
                }

                int btnW = Theme.S(84), btnH = Theme.S(28), gap = Theme.S(10);
                int total = btnW * 2 + gap;
                int bx = midX - total / 2;
                int by = midY + Theme.S(24);
                _btnReconnect = new Rectangle(bx, by, btnW, btnH);
                _btnClose = new Rectangle(bx + btnW + gap, by, btnW, btnH);

                DrawButton(g, _btnReconnect, "重连", true, _hot == 1);
                DrawButton(g, _btnClose, "关闭", false, _hot == 2);
            }

            private void DrawButton(Graphics g, Rectangle r, string text, bool primary, bool hover)
            {
                if (primary)
                {
                    using (var path = Theme.RoundRect(r, Theme.S(6)))
                    using (var b = new SolidBrush(hover ? Theme.AccentHover : Theme.Accent))
                        g.FillPath(b, path);
                    Theme.DrawText(g, text, UiFonts.UI(12f, FontStyle.Bold), Theme.TextInverse, r,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                }
                else
                {
                    Theme.FillRound(g, r, Theme.S(6), hover ? Theme.Bg3 : Theme.Bg2);
                    Theme.DrawRound(g, r, Theme.S(6), hover ? Theme.BorderStrong : Theme.Border);
                    Theme.DrawText(g, text, UiFonts.UI(12f, FontStyle.Regular), Theme.TextSecondary, r,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                }
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                int hot = _btnReconnect.Contains(e.Location) ? 1
                        : _btnClose.Contains(e.Location) ? 2 : 0;
                if (hot != _hot)
                {
                    _hot = hot;
                    Cursor = hot == 0 ? Cursors.Default : Cursors.Hand;
                    Invalidate();
                }
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                _hot = 0;
                Cursor = Cursors.Default;
                Invalidate();
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left || Owner == null) return;
                if (_btnReconnect.Contains(e.Location)) Owner.Reconnect();
                else if (_btnClose.Contains(e.Location))
                {
                    var handler = Owner.CloseRequested;
                    if (handler != null) handler(Owner);
                }
            }
        }

        // ------------------------------------------------------------------
        // Win32
        // ------------------------------------------------------------------

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y,
                                                int cx, int cy, uint flags);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLength(IntPtr hwnd);

        private static string WindowTitle(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return "";
            try
            {
                int len = GetWindowTextLength(hwnd);
                if (len <= 0) return "";
                var sb = new StringBuilder(len + 2);
                GetWindowText(hwnd, sb, sb.Capacity);
                return sb.ToString();
            }
            catch { return ""; }
        }

        public static bool TitleIsInactive(IntPtr hwnd)
        {
            string t = WindowTitle(hwnd);
            return t.IndexOf("inactive", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
