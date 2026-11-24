using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace XPutty
{
    public class ItemPaintEventArgs : EventArgs
    {
        public Graphics G;
        public Rectangle Bounds;
        public object Item;
        public int Index;
        public bool Selected;
        public bool Hover;
        public bool Dragging;

        /// <summary>Alias for G (friendlier for external callers).</summary>
        public Graphics Graphics { get { return G; } }
    }

    /// <summary>
    /// Generic self-drawn scrolling list: variable row heights, overlay scrollbar,
    /// empty state, hover/selection, double-click activation, optional drag
    /// reordering with insertion indicator.  The caller paints each row.
    /// </summary>
    public class ScrollList : Control
    {
        public readonly List<object> Items = new List<object>();

        public Func<object, int> RowHeight;
        public int TopPad = 0;
        public int BottomPad = 0;
        public int SelectedIndex = -1;
        public string EmptyText = "";
        public bool AllowReorder;
        public bool ShowScrollbar = true;
        public int WheelRows = 3;

        public event EventHandler SelectionChanged;
        public event Action<object> ItemActivated;
        public event EventHandler<ItemPaintEventArgs> PaintItem;
        public event Action<int, int> Reordered;

        private int _scroll;
        private int _hover = -1;
        private bool _thumbHover, _thumbDrag;
        private int _thumbDragOffset;
        private int _dragIndex = -1;
        private int _dragTarget = -1;
        private Point _dragStart;
        private bool _dragging;

        public ScrollList()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                     | ControlStyles.Selectable, true);
            BackColor = UiKit.ListBg;
            TabStop = true;
        }

        public object SelectedItem
        {
            get { return SelectedIndex >= 0 && SelectedIndex < Items.Count ? Items[SelectedIndex] : null; }
        }

        public int ScrollOffset { get { return _scroll; } }

        public void SetItems(IEnumerable<object> items)
        {
            Items.Clear();
            if (items != null) Items.AddRange(items);
            if (SelectedIndex >= Items.Count) SelectedIndex = Items.Count - 1;
            _scroll = 0;
            Invalidate();
        }

        public void InvalidateList()
        {
            ClampScroll();
            Invalidate();
        }

        public int RowHeightOf(int index)
        {
            if (index < 0 || index >= Items.Count) return 0;
            int h = RowHeight != null ? RowHeight(Items[index]) : Theme.S(36);
            return Math.Max(Theme.S(8), h);
        }

        private int ContentHeight()
        {
            int y = TopPad + BottomPad;
            for (int i = 0; i < Items.Count; i++) y += RowHeightOf(i);
            return y;
        }

        private int MaxScroll()
        {
            return Math.Max(0, ContentHeight() - ClientSize.Height);
        }

        private void ClampScroll()
        {
            _scroll = Math.Max(0, Math.Min(MaxScroll(), _scroll));
        }

        public void ScrollTo(int px)
        {
            _scroll = Math.Max(0, Math.Min(MaxScroll(), px));
            Invalidate();
        }

        public Rectangle ItemBounds(int index)
        {
            int y = TopPad - _scroll;
            for (int i = 0; i < index && i < Items.Count; i++) y += RowHeightOf(i);
            return new Rectangle(0, y, ClientSize.Width, RowHeightOf(index));
        }

        public int ItemAt(Point p)
        {
            int y = TopPad - _scroll;
            for (int i = 0; i < Items.Count; i++)
            {
                int h = RowHeightOf(i);
                if (p.Y >= y && p.Y < y + h) return i;
                y += h;
            }
            return -1;
        }

        public void EnsureVisible(int index)
        {
            if (index < 0 || index >= Items.Count) return;
            Rectangle r = ItemBounds(index);
            if (r.Top < 0) ScrollTo(_scroll + r.Top);
            else if (r.Bottom > ClientSize.Height) ScrollTo(_scroll + r.Bottom - ClientSize.Height);
        }

        public void Select(int index, bool fireEvent)
        {
            index = Math.Max(-1, Math.Min(Items.Count - 1, index));
            if (index == SelectedIndex) return;
            SelectedIndex = index;
            if (index >= 0) EnsureVisible(index);
            Invalidate();
            if (fireEvent)
            {
                var h = SelectionChanged;
                if (h != null) h(this, EventArgs.Empty);
            }
        }

        // ---- input -----------------------------------------------------
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ScrollTo(_scroll - Math.Sign(e.Delta) * RowHeightOf(Math.Max(0, _hover)) * WheelRows);
            UpdateHover(e.Location);
        }

        private void UpdateHover(Point p)
        {
            int h = ItemAt(p);
            if (h != _hover) { _hover = h; Invalidate(); }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_dragIndex >= 0)
            {
                if (!_dragging && AllowReorder)
                {
                    int dx = e.X - _dragStart.X, dy = e.Y - _dragStart.Y;
                    if (dx * dx + dy * dy > Theme.S(6) * Theme.S(6)) _dragging = true;
                }
                if (_dragging)
                {
                    int target = DragTargetAt(e.Y);
                    if (target != _dragTarget) { _dragTarget = target; Invalidate(); }
                    return;
                }
            }
            if (_thumbDrag) { DragThumbTo(e.Y); return; }
            UpdateHover(e.Location);
            bool overThumb = ThumbRect.Contains(e.Location) && HasScrollbar();
            if (overThumb != _thumbHover) { _thumbHover = overThumb; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = -1; _thumbHover = false;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button == MouseButtons.Left && HasScrollbar() && ThumbRect.Contains(e.Location))
            {
                _thumbDrag = true;
                _thumbDragOffset = e.Y - ThumbRect.Y;
                Capture = true;
                return;
            }
            int index = ItemAt(e.Location);
            if (index >= 0)
            {
                Select(index, true);
                _dragIndex = index;
                _dragTarget = index;
                _dragStart = e.Location;
                if (AllowReorder) Capture = true;
            }
            else
            {
                _dragIndex = -1;
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            Capture = false;
            if (_thumbDrag) { _thumbDrag = false; Invalidate(); return; }

            if (_dragIndex >= 0 && _dragging)
            {
                int from = _dragIndex;
                int to = _dragTarget;
                _dragging = false;
                _dragIndex = -1;
                Capture = false;
                Invalidate();
                if (from != to && to >= 0)
                {
                    var h = Reordered;
                    if (h != null) h(from, to);
                }
                return;
            }
            _dragging = false;
            _dragIndex = -1;
            Invalidate();
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture && _dragging) { _dragging = false; _dragIndex = -1; Invalidate(); }
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            int i = SelectedIndex;
            if (i >= 0 && i < Items.Count)
            {
                var h = ItemActivated;
                if (h != null) h(Items[i]);
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (Items.Count == 0) return;
            if (e.KeyCode == Keys.Down)
            {
                Select(Math.Min(Items.Count - 1, SelectedIndex + 1), true);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Up)
            {
                Select(Math.Max(0, SelectedIndex - 1), true);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Enter)
            {
                if (SelectedIndex >= 0)
                {
                    var h = ItemActivated;
                    if (h != null) h(Items[SelectedIndex]);
                }
                e.Handled = true;
            }
        }

        // ---- scrollbar --------------------------------------------------
        private bool HasScrollbar()
        {
            return ShowScrollbar && MaxScroll() > 0;
        }

        private Rectangle ThumbRect
        {
            get
            {
                int w = Theme.S(6);
                int x = ClientSize.Width - w - Theme.S(4);
                int h = ContentHeight();
                if (h <= 0 || ClientSize.Height <= 0) return Rectangle.Empty;
                int trackTop = Theme.S(4), trackH = ClientSize.Height - Theme.S(8);
                int thumbH = Math.Max(Theme.S(28), (int)((long)trackH * ClientSize.Height / Math.Max(1, h)));
                int maxScroll = MaxScroll();
                int pos = maxScroll > 0 ? (int)((long)(trackH - thumbH) * _scroll / maxScroll) : 0;
                return new Rectangle(x, trackTop + pos, w, thumbH);
            }
        }

        private void DragThumbTo(int y)
        {
            int trackH = ClientSize.Height - Theme.S(8);
            var t = ThumbRect;
            int maxScroll = MaxScroll();
            int travel = Math.Max(1, trackH - t.Height);
            int pos = Math.Max(0, Math.Min(travel, y - _thumbDragOffset - Theme.S(4)));
            ScrollTo((int)((long)maxScroll * pos / travel));
        }

        private int DragTargetAt(int y)
        {
            int top = TopPad - _scroll;
            for (int i = 0; i < Items.Count; i++)
            {
                int h = RowHeightOf(i);
                if (y < top + h / 2) return i;
                top += h;
            }
            return Items.Count;
        }

        // ---- paint ------------------------------------------------------
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(BackColor);

            if (Items.Count == 0)
            {
                if (!string.IsNullOrEmpty(EmptyText))
                {
                    var f = UiKit.UI(12f);
                    var r = new Rectangle(Theme.S(12), 0, ClientSize.Width - Theme.S(24), ClientSize.Height);
                    UiKit.Text(g, EmptyText, f, Theme.TextTertiary, r, UiKit.LC);
                }
                return;
            }

            ClampScroll();
            int top = TopPad - _scroll;
            for (int i = 0; i < Items.Count; i++)
            {
                int h = RowHeightOf(i);
                var bounds = new Rectangle(0, top, ClientSize.Width, h);
                if (bounds.Bottom > 0 && bounds.Top < ClientSize.Height)
                {
                    var args = new ItemPaintEventArgs
                    {
                        G = g,
                        Bounds = bounds,
                        Item = Items[i],
                        Index = i,
                        Selected = i == SelectedIndex,
                        Hover = i == _hover,
                        Dragging = _dragging && i == _dragIndex
                    };
                    var handler = PaintItem;
                    if (handler != null) handler(this, args);
                }
                top += h;
            }

            if (_dragging && _dragTarget >= 0)
            {
                int y;
                if (_dragTarget >= Items.Count)
                    y = ItemBounds(Items.Count - 1).Bottom;
                else
                    y = ItemBounds(_dragTarget).Top;
                y = Math.Max(TopPad - _scroll, Math.Min(ClientSize.Height - 1, y));
                using (var pen = new Pen(Theme.Accent, Math.Max(2f, Theme.SF(2f))))
                    g.DrawLine(pen, Theme.S(10), y, ClientSize.Width - Theme.S(14), y);
            }

            if (HasScrollbar())
            {
                var t = ThumbRect;
                if (!t.IsEmpty)
                {
                    Color c = _thumbDrag ? Theme.TextTertiary
                            : _thumbHover ? Theme.BorderStrong
                            : Theme.Border;
                    Theme.FillRound(g, t, t.Width / 2, c);
                }
            }
        }
    }
}
