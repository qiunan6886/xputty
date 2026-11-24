using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace XPutty
{
    /// <summary>Dark dropdown field with a self-drawn popup list.</summary>
    public class FlatDropdown : Control
    {
        private readonly List<string> _items = new List<string>();
        private int _selected = -1;
        private bool _hover;
        private DropdownPopup _popup;

        public string Placeholder = "请选择";
        public int Radius = 6;
        public int ItemHeight = 28;

        public event EventHandler SelectedIndexChanged;

        public FlatDropdown()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                     | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Font = UiKit.UI(12.5f);
            Cursor = Cursors.Hand;
            Height = Theme.S(32);
        }

        public List<string> Items { get { return _items; } }

        public void SetItems(IEnumerable<string> items)
        {
            _items.Clear();
            if (items != null) _items.AddRange(items);
            if (_selected >= _items.Count) _selected = _items.Count - 1;
            Invalidate();
        }

        public int SelectedIndex
        {
            get { return _selected; }
            set
            {
                int v = (value < 0 || value >= _items.Count) ? -1 : value;
                if (v == _selected) return;
                _selected = v;
                Invalidate();
                var h = SelectedIndexChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        public string SelectedText
        {
            get { return _selected >= 0 && _selected < _items.Count ? _items[_selected] : ""; }
        }

        public string Value
        {
            get { return SelectedText; }
            set
            {
                int i = _items.IndexOf(value);
                if (i < 0) i = -1;
                _selected = i;
                Invalidate();
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true; Invalidate(); base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false; Invalidate(); base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left) OpenPopup();
        }

        public void OpenPopup()
        {
            if (_popup != null) { ClosePopup(); return; }
            if (_items.Count == 0) return;
            _popup = new DropdownPopup(this, _items, _selected, ItemHeight);
            _popup.ClosedByPick += delegate(int index)
            {
                SelectedIndex = index;
            };
            _popup.FormClosed += delegate { _popup = null; Invalidate(); };
            Rectangle screen = RectangleToScreen(ClientRectangle);
            int itemH = ThemedItemHeight();
            int h = _items.Count * itemH + Theme.S(8);
            int below = screen.Bottom + Theme.S(4);
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            int y = below;
            if (y + h > wa.Bottom) y = Math.Max(wa.Top, screen.Top - Theme.S(4) - h);
            int w = Math.Max(screen.Width, Theme.S(140));
            _popup.SetPopupBounds(new Rectangle(screen.X, y, w, h), itemH);
            var owner = FindForm();
            if (owner != null) _popup.Show(owner); else _popup.Show();
            _popup.Focus();
        }

        private int ThemedItemHeight()
        {
            return Theme.S(ItemHeight);
        }

        private void ClosePopup()
        {
            if (_popup != null) { _popup.Close(); _popup = null; }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width, Height);
            int rad = Theme.S(Radius);

            Theme.FillRound(g, r, rad, UiKit.FieldBg);
            Theme.DrawRound(g, new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), rad,
                _hover || _popup != null ? Theme.BorderStrong : UiKit.InputBorderColor,
                Math.Max(1f, Theme.SF(1f)));

            var cr = new Rectangle(r.Right - Theme.S(24), r.Y, Theme.S(16), r.Height);
            UiKit.Chevron(g, cr, 0, Theme.TextSecondary);

            string text = SelectedText;
            Color fg = Theme.TextPrimary;
            if (string.IsNullOrEmpty(text)) { text = Placeholder; fg = Theme.TextTertiary; }
            var tr = new Rectangle(r.X + Theme.S(10), r.Y, r.Width - Theme.S(38), r.Height);
            UiKit.Text(g, text, Font, fg, tr, UiKit.LE);
        }

        private sealed class DropdownPopup : Form
        {
            private readonly List<string> _items;
            private int _selected, _hover = -1, _itemH;
            private readonly FlatDropdown _owner;

            public event Action<int> ClosedByPick;

            public DropdownPopup(FlatDropdown owner, List<string> items, int selected, int itemHeight)
            {
                _owner = owner;
                _items = items;
                _selected = selected;
                _itemH = itemHeight;

                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                BackColor = UiKit.DialogBg;
                DoubleBuffered = true;
                KeyPreview = true;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                         | ControlStyles.OptimizedDoubleBuffer, true);
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

            public void SetPopupBounds(Rectangle bounds, int itemH)
            {
                Bounds = bounds;
                _itemH = itemH;
                Region = new Region(Theme.RoundRect(new Rectangle(0, 0, Width, Height), Theme.S(8)));
            }

            protected override void OnDeactivate(EventArgs e)
            {
                base.OnDeactivate(e);
                Close();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var r = new Rectangle(0, 0, Width, Height);
                Theme.FillRound(g, r, Theme.S(8), UiKit.DialogBg);
                Theme.DrawRound(g, new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), Theme.S(8),
                    Theme.BorderStrong, Math.Max(1f, Theme.SF(1f)));

                int pad = Theme.S(4);
                Font f = UiKit.UI(12.5f);
                for (int i = 0; i < _items.Count; i++)
                {
                    var ir = new Rectangle(pad, pad + i * _itemH, Width - pad * 2, _itemH);
                    if (i == _selected)
                        Theme.FillRound(g, new Rectangle(ir.X, ir.Y + Theme.S(1), ir.Width, ir.Height - Theme.S(2)),
                            Theme.S(4), UiKit.ItemSelBg);
                    else if (i == _hover)
                        Theme.FillRound(g, new Rectangle(ir.X, ir.Y + Theme.S(1), ir.Width, ir.Height - Theme.S(2)),
                            Theme.S(4), UiKit.HoverRow);
                    if (i == _selected)
                        UiKit.Check(g, new Rectangle(ir.Right - Theme.S(18), ir.Y, Theme.S(12), ir.Height), Theme.Accent);
                    var tr = new Rectangle(ir.X + Theme.S(8), ir.Y, ir.Width - Theme.S(26), ir.Height);
                    UiKit.Text(g, _items[i], f, i == _selected ? Theme.TextPrimary : Theme.TextSecondary, tr, UiKit.LE);
                }
            }

            private int HitTest(Point p)
            {
                int pad = Theme.S(4);
                int i = (p.Y - pad) / Math.Max(1, _itemH);
                if (i < 0 || i >= _items.Count) return -1;
                return i;
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
                int i = HitTest(e.Location);
                if (i >= 0)
                {
                    _selected = i;
                    var h = ClosedByPick;
                    if (h != null) h(i);
                    Close();
                }
            }

            protected override void OnKeyDown(KeyEventArgs e)
            {
                base.OnKeyDown(e);
                if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; return; }
                if (e.KeyCode == Keys.Enter && _hover >= 0)
                {
                    var h = ClosedByPick;
                    if (h != null) h(_hover);
                    Close(); e.Handled = true; return;
                }
                if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Up)
                {
                    _hover = Math.Max(0, Math.Min(_items.Count - 1, (_hover < 0 ? _selected : _hover) + (e.KeyCode == Keys.Down ? 1 : -1)));
                    Invalidate(); e.Handled = true;
                }
            }
        }
    }
}
