using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace XPutty
{
    /// <summary>
    /// Dark rounded input box.  A borderless TextBox is embedded so caret,
    /// selection, IME and clipboard all behave natively; the surrounding chrome
    /// (fill, 1px border, focus ring, left icon, right key cap) is self-drawn.
    /// </summary>
    public class FlatInput : Control, Theme.IThemeAware
    {
        private readonly TextBox _box;
        private bool _focused;
        private string _placeholder = "";
        private bool _placeholderShown;
        private bool _internalChange;

        public string LeftIcon;
        public string RightHint;
        public int Radius = 6;
        public bool Multiline;

        public FlatInput() : this(false) { }

        public FlatInput(bool multiline)
        {
            Multiline = multiline;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                     | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;

            _box = new TextBox();
            _box.BorderStyle = BorderStyle.None;
            _box.BackColor = UiKit.FieldBg;
            _box.ForeColor = Theme.TextPrimary;
            _box.Font = UiKit.UI(12.5f);
            _box.Multiline = multiline;
            if (multiline)
            {
                _box.AcceptsReturn = true;
                _box.WordWrap = true;
                _box.ScrollBars = ScrollBars.None;
            }
            _box.GotFocus += delegate { _focused = true; HidePlaceholder(); Invalidate(); };
            _box.LostFocus += delegate { _focused = false; ShowPlaceholderIfEmpty(); Invalidate(); };
            _box.TextChanged += BoxTextChanged;
            _box.KeyDown += BoxKeyDown;
            Controls.Add(_box);
        }

        public TextBox Inner { get { return _box; } }

        public void ApplyThemeColors()
        {
            Diag.Log("FlatInput apply theme, field=" + UiKit.FieldBg + " handle=" + _box.IsHandleCreated);
            BackColor = Color.Transparent;
            _box.BackColor = UiKit.FieldBg;
            _box.ForeColor = Theme.TextPrimary;
            ApplyCueBanner();
            Invalidate();
        }

        public override string Text
        {
            get { return _box.Text; }
            set
            {
                _internalChange = true;
                _box.Text = value ?? "";
                _internalChange = false;
                ApplyCueBanner();
            }
        }

        public string Placeholder
        {
            get { return _placeholder; }
            set
            {
                _placeholder = value ?? "";
                ApplyCueBanner();
            }
        }

        // ---- placeholder via the native cue banner -----------------------
        // Drawing the placeholder as real TextBox text was fragile: setting
        // Text on a focused TextBox selects it, which showed up as a black
        // selection band in the light theme.  EM_SETCUEBANNER is native,
        // renders grey and never pollutes the text value.
        [System.Runtime.InteropServices.DllImport("user32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg,
                                                 IntPtr wParam, string lParam);

        private const int EM_SETCUEBANNER = 0x1501;

        private void ApplyCueBanner()
        {
            if (!IsHandleCreated || _box == null || !_box.IsHandleCreated) return;
            if (Multiline) return;
            try
            {
                SendMessage(_box.Handle, EM_SETCUEBANNER, (IntPtr)1, _placeholder ?? "");
            }
            catch { }
        }

        private void BoxTextChanged(object sender, EventArgs e)
        {
            if (_internalChange) return;
            OnTextChanged(EventArgs.Empty);
            Invalidate();
        }

        private void ShowPlaceholderIfEmpty()
        {
            ApplyCueBanner();
        }

        private void HidePlaceholder()
        {
            ApplyCueBanner();
        }

        public bool ReadOnlyValue
        {
            get { return _box.ReadOnly; }
            set
            {
                _box.ReadOnly = value;
                _box.BackColor = UiKit.FieldBg;
                Invalidate();
            }
        }

        public int MaxLength
        {
            get { return _box.MaxLength; }
            set { _box.MaxLength = value; }
        }

        public bool Mono
        {
            set { _box.Font = value ? UiKit.Mono(12f) : UiKit.UI(12.5f); LayoutBox(); }
        }

        public void SelectAllText()
        {
            _box.SelectAll();
        }

        public void CaretToEnd()
        {
            _box.SelectionStart = _box.TextLength;
            _box.SelectionLength = 0;
        }

        public void FocusInput()
        {
            _box.Focus();
        }

        public event EventHandler EnterPressed;
        public event EventHandler EscapePressed;

        private void BoxKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter && !Multiline)
            {
                var h = EnterPressed;
                if (h != null) { h(this, EventArgs.Empty); }
            }
            else if (e.KeyCode == Keys.Escape)
            {
                var h = EscapePressed;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            LayoutBox();
            ShowPlaceholderIfEmpty();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _box.Focus();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutBox();
        }

        private void LayoutBox()
        {
            if (_box == null) return;
            int padL = Theme.S(10) + (string.IsNullOrEmpty(LeftIcon) ? 0 : Theme.S(18));
            int padR = Theme.S(10) + (string.IsNullOrEmpty(RightHint) ? 0 : MeasureCap());
            int w = Math.Max(Theme.S(10), Width - padL - padR);
            if (Multiline)
            {
                _box.SetBounds(padL, Theme.S(6), w, Math.Max(Theme.S(14), Height - Theme.S(12)));
            }
            else
            {
                int ph = Math.Max(Theme.S(14), _box.PreferredHeight);
                _box.SetBounds(padL, Math.Max(0, (Height - ph) / 2), w, ph);
            }
        }

        private int MeasureCap()
        {
            if (string.IsNullOrEmpty(RightHint)) return 0;
            Font f = UiKit.Mono(9.5f);
            return UiKit.Measure(RightHint, f) + Theme.S(10) + Theme.S(8);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width, Height);
            int rad = Theme.S(Radius);

            Theme.FillRound(g, r, rad, UiKit.FieldBg);

            if (_focused)
            {
                var glow = new Rectangle(r.X - Theme.S(3), r.Y - Theme.S(3),
                                         r.Width + Theme.S(6), r.Height + Theme.S(6));
                Theme.FillRound(g, glow, rad + Theme.S(3), Color.FromArgb(32, Theme.Accent));
            }
            Theme.DrawRound(g, new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), rad,
                _focused ? Theme.FocusLine : UiKit.InputBorderColor, Math.Max(1f, Theme.SF(1f)));

            int iconSize = Theme.S(14);
            int cy = r.Y + r.Height / 2;
            int x = r.X + Theme.S(10);
            if (!string.IsNullOrEmpty(LeftIcon))
            {
                UiKit.Icon(g, LeftIcon, new Rectangle(x, cy - iconSize / 2, iconSize, iconSize),
                    _focused ? Theme.TextSecondary : Theme.TextTertiary);
                x += Theme.S(18);
            }

            if (!string.IsNullOrEmpty(RightHint))
            {
                Font capFont = UiKit.Mono(9.5f);
                var cap = UiKit.KeyCapRect(g, r.Right - Theme.S(10), cy, RightHint, capFont);
                UiKit.KeyCap(g, cap, RightHint, capFont, Theme.TextTertiary, UiKit.KeyCapBorder);
            }
        }
    }
}
