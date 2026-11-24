using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace XPutty
{
    /// <summary>
    /// Segmented selector (global / current-session, dark / light, protocol kind).
    /// Container is the input field look, the selected cell gets the raised fill.
    /// </summary>
    public class SegmentedControl : Control
    {
        private string[] _options = new string[0];
        private int _selected;
        private int _hover = -1;
        private bool[] _enabled = new bool[0];
        public int Radius = 6;

        public event EventHandler SelectedIndexChanged;

        public SegmentedControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                     | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Font = UiKit.UI(12f);
            Cursor = Cursors.Hand;
            Height = Theme.S(32);
        }

        public string[] Options
        {
            get { return _options; }
            set
            {
                _options = value ?? new string[0];
                _enabled = new bool[_options.Length];
                for (int i = 0; i < _enabled.Length; i++) _enabled[i] = true;
                _selected = Math.Max(0, Math.Min(_selected, _options.Length - 1));
                Invalidate();
            }
        }

        public int SelectedIndex
        {
            get { return _selected; }
            set
            {
                if (_options.Length == 0) { _selected = 0; return; }
                int v = Math.Max(0, Math.Min(_options.Length - 1, value));
                if (v == _selected) return;
                _selected = v;
                Invalidate();
                var h = SelectedIndexChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        public void SetOptionEnabled(int index, bool enabled)
        {
            if (index < 0 || index >= _enabled.Length) return;
            _enabled[index] = enabled;
            Invalidate();
        }

        public bool IsOptionEnabled(int index)
        {
            return index >= 0 && index < _enabled.Length && _enabled[index];
        }

        private Rectangle CellRect(int i)
        {
            int n = Math.Max(1, _options.Length);
            int w = Width / n;
            int x = i * w;
            if (i == n - 1) w = Width - x;
            return new Rectangle(x, 0, w, Height);
        }

        private int HitTest(Point p)
        {
            for (int i = 0; i < _options.Length; i++)
                if (CellRect(i).Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = HitTest(e.Location);
            if (h != _hover) { _hover = h; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = -1; Invalidate(); base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            int i = HitTest(e.Location);
            if (i >= 0 && IsOptionEnabled(i) && i != _selected)
            {
                _selected = i;
                Invalidate();
                var h = SelectedIndexChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width, Height);
            int rad = Theme.S(Radius);

            Theme.FillRound(g, r, rad, UiKit.FieldBg);
            Theme.DrawRound(g, new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), rad, UiKit.InputBorderColor,
                Math.Max(1f, Theme.SF(1f)));

            for (int i = 0; i < _options.Length; i++)
            {
                Rectangle cell = CellRect(i);
                bool sel = i == _selected;
                bool enabled = IsOptionEnabled(i);
                if (sel)
                {
                    var sr = new Rectangle(cell.X + Theme.S(2), cell.Y + Theme.S(2),
                                           cell.Width - Theme.S(4), cell.Height - Theme.S(4));
                    int srad = Math.Max(Theme.S(2), rad - Theme.S(2));
                    Theme.FillRound(g, sr, srad, UiKit.ItemSelBg);
                    Theme.DrawRound(g, new Rectangle(sr.X, sr.Y, sr.Width - 1, sr.Height - 1), srad,
                        UiKit.ItemSelBorder, Math.Max(1f, Theme.SF(1f)));
                }
                else if (i == _hover && enabled)
                {
                    var sr = new Rectangle(cell.X + Theme.S(2), cell.Y + Theme.S(2),
                                           cell.Width - Theme.S(4), cell.Height - Theme.S(4));
                    Theme.FillRound(g, sr, Math.Max(Theme.S(2), rad - Theme.S(2)),
                        Color.FromArgb(12, 255, 255, 255));
                }

                Color fg = !enabled ? Theme.TextDisabled
                         : sel ? Theme.TextPrimary : Theme.TextSecondary;
                UiKit.Text(g, _options[i], Font, fg, cell, UiKit.LC);
            }
        }
    }
}
