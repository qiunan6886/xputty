using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace XPutty
{
    /// <summary>Panel with a custom paint hook (used for dark list/footer strips).</summary>
    public class PaintPanel : Panel
    {
        public Action<Graphics, Rectangle> Painter;

        public PaintPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            if (Painter != null) Painter(e.Graphics, ClientRectangle);
        }
    }

    /// <summary>
    /// Full-owner-size translucent scrim shown behind a modal dialog.  It never
    /// activates, never closes the dialog when clicked (design: click outside is
    /// ignored so a stray click cannot discard edits) and fades in over 120ms.
    /// </summary>
    internal class ModalScrim : Form
    {
        private readonly Timer _fade;
        private float _t;

        public ModalScrim(IWin32Window owner)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(5, 7, 11);
            Opacity = 0;
            DoubleBuffered = true;
            Bounds = ComputeBounds(owner);

            _fade = new Timer();
            _fade.Interval = 15;
            _fade.Tick += delegate
            {
                _t += 0.12f;
                if (_t >= 1f) { _t = 1f; _fade.Stop(); }
                Opacity = 0.62 * _t;
            };
        }

        private static Rectangle ComputeBounds(IWin32Window owner)
        {
            var f = owner as Form;
            if (f != null && f.WindowState != FormWindowState.Minimized)
                return f.Bounds;
            var f2 = owner as Control;
            if (f2 != null) return f2.RectangleToScreen(f2.ClientRectangle);
            return Screen.PrimaryScreen.Bounds;
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                return cp;
            }
        }

        public void StartFade()
        {
            if (!IsHandleCreated) return;
            _fade.Start();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && _fade != null) _fade.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Borderless dark dialog base: 1px border + 8px rounded corners, 62px
    /// header (icon block, title, subtitle, close), custom body panel, 58px
    /// footer with right-aligned buttons, Esc = cancel, header drag, scrim.
    /// </summary>
    public class ModalForm : Form
    {
        public bool Accepted;

        protected readonly PaintPanel Body;
        protected readonly PaintPanel Footer;
        private readonly ChromeButton _close;
        private readonly List<ChromeButton> _footerButtons = new List<ChromeButton>();
        private readonly List<bool> _footerPrimary = new List<bool>();

        private int _logicalW = 760, _logicalH = 584;
        private ModalScrim _scrim;

        protected string TitleText = "";
        protected string SubtitleText = "";
        protected string HeaderIconKind = "gear";
        protected string FooterLeftText = "";
        protected Color FooterLeftColor = Theme.TextSecondary;
        protected bool ShowHeaderIcon = true;

        public ModalForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            KeyPreview = true;
            DoubleBuffered = true;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = UiKit.DialogBg;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

            Body = new PaintPanel();
            Body.BackColor = UiKit.DialogBg;
            Controls.Add(Body);

            Footer = new PaintPanel();
            Footer.BackColor = UiKit.FooterBg;
            Footer.Painter = PaintFooter;
            Controls.Add(Footer);

            _close = new ChromeButton();
            _close.Style = ChromeStyle.Ghost;
            _close.Icon = "close";
            _close.IconOnly = true;
            _close.Radius = 4;
            _close.Cursor = Cursors.Hand;
            _close.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(_close);
            _close.BringToFront();
            _close.Size = new Size(Theme.S(26), Theme.S(26));
        }

        // ---- derived metrics -------------------------------------------
        protected int HeaderH { get { return Theme.S(62); } }
        protected int FooterH { get { return Theme.S(58); } }

        protected void SetDialogSize(int logicalWidth, int logicalHeight)
        {
            _logicalW = logicalWidth;
            _logicalH = logicalHeight;
            ApplyDialogSize();
        }

        private void ApplyDialogSize()
        {
            ClientSize = new Size(Theme.S(_logicalW), Theme.S(_logicalH));
            LayoutChrome();
        }

        private void LayoutChrome()
        {
            if (Body == null || Footer == null) return;
            int w = ClientSize.Width, h = ClientSize.Height;
            Body.SetBounds(0, HeaderH, w, Math.Max(0, h - HeaderH - FooterH));
            Footer.SetBounds(0, h - FooterH, w, FooterH);
            _close.SetBounds(w - Theme.S(20) - Theme.S(26), Theme.S(18), Theme.S(26), Theme.S(26));
            LayoutFooterButtons();
            ApplyRoundedRegion();
        }

        protected void LayoutFooterButtons()
        {
            int right = Footer.ClientSize.Width - Theme.S(20);
            int cy = Footer.ClientSize.Height / 2;
            for (int i = _footerButtons.Count - 1; i >= 0; i--)
            {
                var b = _footerButtons[i];
                int bw = Math.Max(Theme.S(72), UiKit.Measure(b.Text, b.Font) + Theme.S(34));
                int bh = Theme.S(32);
                right -= bw;
                b.SetBounds(right, cy - bh / 2, bw, bh);
                right -= Theme.S(8);
            }
            if (Footer.Painter == null) Footer.Painter = PaintFooter;
        }

        protected ChromeButton AddFooterButton(string text, bool primary, EventHandler onClick)
        {
            var b = new ChromeButton();
            b.Style = primary ? ChromeStyle.Primary : ChromeStyle.Flat;
            b.Text = text;
            b.Font = UiKit.UI(12.5f);
            if (primary) b.Icon = "check";
            b.Click += onClick;
            Footer.Controls.Add(b);
            _footerButtons.Add(b);
            _footerPrimary.Add(primary);
            LayoutFooterButtons();
            return b;
        }

        protected ChromeButton FooterButton(int index)
        {
            return index >= 0 && index < _footerButtons.Count ? _footerButtons[index] : null;
        }

        /// <summary>Apply the 8px rounded window region (also on resize).</summary>
        protected void ApplyRoundedRegion()
        {
            if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            using (var path = Theme.RoundRect(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height), Theme.S(8)))
                Region = new Region(path);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // NOTE: do not re-derive Theme.Scale from Control.DeviceDpi here.
            // On .NET Framework 4.8 Control.DeviceDpi reports 96 unless the app
            // opts into PerMonitorV2 through app.config, while the real monitor
            // DPI (and Program's Graphics-based scale) is 192.  The dialogs use
            // the scale that Program / MainForm already established.
            ApplyDialogSize();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutChrome();
        }

        // ---- window chrome --------------------------------------------
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(UiKit.DialogBg);

            // header
            int x = Theme.S(20);
            int cy = Theme.S(31);
            if (ShowHeaderIcon)
            {
                var ir = new Rectangle(x, cy - Theme.S(14), Theme.S(28), Theme.S(28));
                Theme.FillRound(g, ir, Theme.S(6), Color.FromArgb(36, Theme.Accent));
                Theme.DrawRound(g, ir, Theme.S(6), Color.FromArgb(61, Theme.Accent), Math.Max(1f, Theme.SF(1f)));
                UiKit.Icon(g, HeaderIconKind,
                    new Rectangle(ir.X + Theme.S(7), ir.Y + Theme.S(7), Theme.S(14), Theme.S(14)), Theme.Accent);
                x += Theme.S(28) + Theme.S(12);
            }
            UiKit.Text(g, TitleText, UiKit.UIBold(15f), Theme.TextPrimary,
                new Rectangle(x, Theme.S(15), ClientSize.Width - x - Theme.S(60), Theme.S(22)), UiKit.L);
            UiKit.Text(g, SubtitleText, UiKit.UI(11.5f), Theme.TextSecondary,
                new Rectangle(x, Theme.S(36), ClientSize.Width - x - Theme.S(60), Theme.S(16)), UiKit.LE);

            UiKit.LineH(g, 0, ClientSize.Width, HeaderH, UiKit.HeaderDivider);
            UiKit.LineH(g, 0, ClientSize.Width, ClientSize.Height - FooterH, UiKit.HeaderDivider);

            // window border
            Theme.DrawRound(g, new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1),
                Theme.S(8), Theme.BorderStrong, Math.Max(1f, Theme.SF(1f)));
        }

        private void PaintFooter(Graphics g, Rectangle r)
        {
            if (string.IsNullOrEmpty(FooterLeftText)) return;
            UiKit.Text(g, FooterLeftText, UiKit.UI(10.5f), FooterLeftColor,
                new Rectangle(Theme.S(20), 0, r.Width - Theme.S(200), r.Height), UiKit.L);
        }

        // ---- small layout helpers -------------------------------------
        protected Label AddLabel(string text, int x, int y, int w, int h, Color c, float px,
                                 bool bold = false, ContentAlignment align = ContentAlignment.MiddleLeft)
        {
            var l = new Label();
            l.Text = text;
            l.AutoSize = false;
            l.BackColor = Color.Transparent;
            l.ForeColor = c;
            l.Font = bold ? UiKit.UIBold(px) : UiKit.UI(px);
            l.TextAlign = align;
            l.SetBounds(x, y, w, h);
            l.UseMnemonic = false;
            Body.Controls.Add(l);
            return l;
        }

        protected Label Caption(string text, int x, int y, int w = 0)
        {
            if (w <= 0) w = Theme.S(400);
            return AddLabel(text, x, y, w, Theme.S(15), Theme.TextSecondary, 11.5f);
        }

        protected Label HintRight(string text, int rightEdgeX, int y, int w)
        {
            int x = Math.Max(0, rightEdgeX - w);
            return AddLabel(text, x, y, w, Theme.S(15), Theme.TextTertiary, 10.5f, false,
                ContentAlignment.MiddleRight);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && e.Y < HeaderH)
            {
                UiKit.ReleaseCapture();
                UiKit.SendMessage(Handle, UiKit.WM_NCLBUTTONDOWN, (IntPtr)UiKit.HTCAPTION, IntPtr.Zero);
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                Close();
                e.Handled = true;
            }
        }

        // ---- modal behaviour ------------------------------------------
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            ApplyDialogSize();

            var owner = Owner;
            if (owner != null)
            {
                Rectangle ob = owner.Bounds;
                Rectangle wa = PhysicalWorkArea(ob, Screen.FromRectangle(ob).WorkingArea);
                int x = ob.X + (ob.Width - Width) / 2;
                int y = ob.Y + (ob.Height - Height) / 2;
                if (ob.Height <= 0) { x = wa.X + (wa.Width - Width) / 2; y = wa.Y + (wa.Height - Height) / 2; }
                x = Math.Max(wa.X, Math.Min(x, wa.Right - Width));
                y = Math.Max(wa.Y, Math.Min(y, wa.Bottom - Height));
                if (y < wa.Y) y = wa.Y;
                Location = new Point(x, y);
            }
            else
            {
                StartPosition = FormStartPosition.CenterScreen;
            }
        }

        /// <summary>
        /// On .NET Framework 4.8 WinForms reports the working area of a 200%
        /// monitor in LOGICAL pixels (its internal DPI stays 96) while our
        /// windows live in physical pixels.  Detect that mismatch (the owner is
        /// wider than the reported work area) and scale the rectangle up.
        /// </summary>
        private static Rectangle PhysicalWorkArea(Rectangle ownerPhysical, Rectangle work)
        {
            if (work.Width <= 0 || work.Height <= 0) return ownerPhysical;
            if (ownerPhysical.Width > work.Width && Theme.Scale > 1.01f)
            {
                return new Rectangle(
                    (int)Math.Round(work.X * Theme.Scale),
                    (int)Math.Round(work.Y * Theme.Scale),
                    (int)Math.Round(work.Width * Theme.Scale),
                    (int)Math.Round(work.Height * Theme.Scale));
            }
            return work;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (Owner != null)
            {
                try { OpenScrim(); } catch { _scrim = null; }
            }
            BringToFront();
            Activate();
        }

        private void OpenScrim()
        {
            if (_scrim != null) return;
            var ownerForm = Owner;
            _scrim = new ModalScrim(Owner);
            // Owned by the main window: an owned window always stays above its
            // owner, and after BringToFront() the dialog sits above the scrim.
            if (ownerForm != null) _scrim.Owner = ownerForm;
            _scrim.Show();
            _scrim.StartFade();
            BringToFront();
            Activate();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            CloseScrim();
            base.OnFormClosed(e);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
        }

        private void CloseScrim()
        {
            if (_scrim == null) return;
            var s = _scrim;
            _scrim = null;
            try { s.Hide(); s.Dispose(); } catch { }
        }

        /// <summary>Convenience: show modally over an owner (null ok).</summary>
        public DialogResult ShowModal(IWin32Window owner)
        {
            return owner != null ? ShowDialog(owner) : ShowDialog();
        }
    }
}
