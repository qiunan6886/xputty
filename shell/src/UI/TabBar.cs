using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace XPutty
{
    /// <summary>Everything the tab bar needs to draw one tab.</summary>
    public class TabInfo
    {
        public string Title = "新标签";
        public string Subtitle = "";
        public ProtocolKind Protocol = ProtocolKind.Ssh;
        public bool Connected;
        public int PaneCount = 1;
        public SessionDef Session;
    }

    /// <summary>
    /// 38px custom-drawn tab strip: tabs (32px, max 210px, bottom aligned),
    /// close buttons, drag re-ordering, middle-click close, new-tab button and
    /// the split / layout / more button group on the right.
    /// </summary>
    public class TabBar : Control
    {
        private const int TabHeight = 32;
        private const int TabMaxWidth = 210;
        private const int TabMinWidth = 96;
        private const int TabTightWidth = 76;
        private const int TabSpacing = 2;

        private List<TabInfo> _tabs = new List<TabInfo>();
        private int _active = -1;

        private readonly List<Rectangle> _tabRects = new List<Rectangle>();
        private readonly List<Rectangle> _closeRects = new List<Rectangle>();
        private Rectangle _btnPlus;
        private Rectangle _btnSplitV;
        private Rectangle _btnSplitH;
        private Rectangle _btnLayout;
        private Rectangle _btnMore;
        private Rectangle _labelSplits;

        private int _scroll;

        private enum Kind { None, Tab, Close, Plus, SplitV, SplitH, Layout, More }

        private Kind _hoverKind = Kind.None;
        private int _hoverTab = -1;
        private Kind _pressedKind = Kind.None;
        private int _pressedTab = -1;

        private int _dragTab = -1;
        private int _dragInsert = -1;
        private bool _dragging;
        private Point _dragStart;

        /// <summary>Tab was clicked (index).</summary>
        public event Action<int> TabSelected;
        /// <summary>Close requested (index).</summary>
        public event Action<int> TabCloseRequested;
        /// <summary>+ button / double click on empty space.</summary>
        public event Action NewTabRequested;
        /// <summary>Drag re-order: from, to.</summary>
        public event Action<int, int> TabMoved;
        /// <summary>Split current pane; true = side by side (vertical divider).</summary>
        public event Action<bool> SplitRequested;
        /// <summary>Layout preset chosen in the layout menu.</summary>
        public event Action<LayoutPreset> LayoutPresetRequested;
        /// <summary>Right-hand "⋯" menu.</summary>
        public event Action MoreRequested;

        public TabBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg1;
            Height = Theme.S(38);
        }

        public List<TabInfo> Tabs { get { return _tabs; } }
        public int ActiveIndex { get { return _active; } }
        public int TabCount { get { return _tabs.Count; } }

        public void SetTabs(List<TabInfo> tabs, int active)
        {
            _tabs = tabs ?? new List<TabInfo>();
            _active = (_tabs.Count == 0) ? -1 : Math.Max(0, Math.Min(_tabs.Count - 1, active));
            if (_scroll > 0) _scroll = 0;
            if (_hoverTab >= _tabs.Count) _hoverTab = -1;
            RecalcLayout();
            Invalidate();
        }

        public void SetActive(int index)
        {
            if (index < 0 || index >= _tabs.Count || index == _active) return;
            _active = index;
            Invalidate();
        }

        public void InvalidateTabs()
        {
            RecalcLayout();
            Invalidate();
        }

        // ------------------------------------------------------------------
        // Layout of the static pieces
        // ------------------------------------------------------------------

        private void RecalcLayout()
        {
            _tabRects.Clear();
            _closeRects.Clear();

            int h = Height;
            int tabH = Theme.S(TabHeight);
            int top = h - tabH - Theme.S(1);
            int spacing = Theme.S(TabSpacing);

            // right hand button group (measured from the right edge)
            int btnW = Theme.S(26);
            int btnH = Theme.S(20);
            int btnGap = Theme.S(3);
            int rightPad = Theme.S(8);
            int btnY = top + (tabH - btnH) / 2;

            int moreX = Width - rightPad - btnW;
            _btnMore = new Rectangle(moreX, btnY, btnW, btnH);
            int layX = moreX - btnGap - btnW;
            _btnLayout = new Rectangle(layX, btnY, btnW, btnH);
            int shX = layX - btnGap - btnW;
            _btnSplitH = new Rectangle(shX, btnY, btnW, btnH);
            int svX = shX - btnGap - btnW;
            _btnSplitV = new Rectangle(svX, btnY, btnW, btnH);

            int labelW = Theme.S(24);
            _labelSplits = new Rectangle(svX - Theme.S(6) - labelW, btnY, labelW, btnH);

            // "+" new tab button
            int plusSize = Theme.S(26);
            int plusX = Width - rightPad - btnW * 4 - btnGap * 3 - labelW - Theme.S(12);
            // (recomputed after tabs below, keeping the right group fixed)

            int leftPad = Theme.S(2);

            // measure natural widths
            using (var font = UiFonts.UI(12.5f, FontStyle.Regular))
            {
                int maxTab = Theme.S(TabMaxWidth);
                int minTab = Theme.S(TabMinWidth);
                var natural = new List<int>();
                int total = 0;
                for (int i = 0; i < _tabs.Count; i++)
                {
                    string title = TrimToMeasure(_tabs[i], font);
                    int textW = TextRenderer.MeasureText(title, font).Width;
                    int w = Theme.S(10) + Theme.S(14) + Theme.S(6) + textW +
                            Theme.S(6) + Theme.S(16) + Theme.S(8);
                    w = Math.Max(minTab, Math.Min(maxTab, w));
                    natural.Add(w);
                    total += w + spacing;
                }

                int availRight = _labelSplits.X - Theme.S(10);
                int avail = Math.Max(0, availRight - leftPad - Theme.S(30));
                if (total > avail && _tabs.Count > 0)
                {
                    // first shrink towards TabTightWidth, then scroll
                    int minTotal = 0;
                    for (int i = 0; i < natural.Count; i++)
                        minTotal += Theme.S(TabTightWidth) + spacing;
                    if (minTotal <= avail)
                    {
                        float k = 1f;
                        int room = avail - spacing * natural.Count;
                        int sum = 0;
                        for (int i = 0; i < natural.Count; i++) sum += natural[i];
                        if (sum > 0) k = Math.Max((float)Theme.S(TabTightWidth) / Theme.S(TabMinWidth),
                                                 Math.Min(1f, (float)room / sum));
                        for (int i = 0; i < natural.Count; i++)
                            natural[i] = Math.Max(Theme.S(TabTightWidth), (int)(natural[i] * k));
                    }
                }
                else
                {
                    _scroll = 0;
                }

                int x = leftPad - _scroll;
                for (int i = 0; i < natural.Count; i++)
                {
                    int w = natural[i];
                    var r = new Rectangle(x, top, w, tabH);
                    _tabRects.Add(r);
                    var cr = new Rectangle(r.Right - Theme.S(22), r.Y + (tabH - Theme.S(16)) / 2,
                                           Theme.S(16), Theme.S(16));
                    _closeRects.Add(cr);
                    x += w + spacing;
                }

                int tabsRight = _tabs.Count > 0 ? _tabRects[_tabRects.Count - 1].Right : leftPad;
                int plusX2 = Math.Min(tabsRight + Theme.S(6), _labelSplits.X - Theme.S(34));
                _btnPlus = new Rectangle(Math.Max(leftPad, plusX2), btnY, plusSize, plusSize);
                _btnPlus.Height = Theme.S(26);
                _btnPlus.Y = top + (tabH - Theme.S(26)) / 2;
            }
        }

        private static string TrimToMeasure(TabInfo info, Font font)
        {
            string title = info == null || info.Title == null ? "新标签" : info.Title;
            return title;
        }

        // ------------------------------------------------------------------
        // Painting
        // ------------------------------------------------------------------

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Bg1);

            int bottomLine = Height - Theme.S(1);
            using (var pen = new Pen(Theme.BorderSoft))
                g.DrawLine(pen, 0, bottomLine, Width, bottomLine);

            // tabs
            for (int i = 0; i < _tabRects.Count; i++)
            {
                Rectangle r = _tabRects[i];
                if (r.Right < -Theme.S(10) || r.Left > Width) continue;
                DrawTab(g, i, r);
            }

            // "+" button
            DrawPlus(g);

            // right group
            using (var font = UiFonts.UI(10.5f, FontStyle.Regular))
            {
                int labelH = TextRenderer.MeasureText("分屏", font).Height;
                Theme.DrawText(g, "分屏", font, Theme.TextTertiary,
                    new Rectangle(_labelSplits.X, _labelSplits.Y + (_labelSplits.Height - labelH) / 2,
                                  _labelSplits.Width, labelH),
                    TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix |
                    TextFormatFlags.Right);
            }
            DrawToolButton(g, _btnSplitV, "split-v", Kind.SplitV);
            DrawToolButton(g, _btnSplitH, "split-h", Kind.SplitH);
            DrawToolButton(g, _btnLayout, "grid", Kind.Layout);
            DrawToolButton(g, _btnMore, "more", Kind.More);

            // drag insertion marker
            if (_dragging && _dragInsert >= 0)
            {
                int x;
                if (_dragInsert >= _tabRects.Count) x = _tabRects.Count > 0 ? _tabRects[_tabRects.Count - 1].Right + Theme.S(1) : Theme.S(2);
                else x = _tabRects[_dragInsert].Left - Theme.S(1);
                using (var pen = new Pen(Theme.Accent, Math.Max(1f, Theme.SF(2))))
                    g.DrawLine(pen, x, _tabRects.Count > 0 ? _tabRects[0].Top + Theme.S(4) : 0,
                               x, bottomLine - Theme.S(2));
            }
        }

        private void DrawTab(Graphics g, int index, Rectangle r)
        {
            bool active = index == _active;
            bool hover = index == _hoverTab;

            int radius = Theme.S(6);
            var body = new Rectangle(r.X, r.Y, r.Width, r.Height + radius); // squared bottom

            if (active)
            {
                using (var path = TopRoundRect(r, radius))
                using (var b = new SolidBrush(Theme.TermBg))
                    g.FillPath(b, path);
                using (var path = TopRoundRect(r, radius))
                using (var pen = new Pen(Theme.Border))
                    g.DrawPath(pen, path);
            }
            else if (hover)
            {
                Theme.FillRound(g, new Rectangle(r.X, r.Y + Theme.S(2), r.Width, r.Height - Theme.S(2)),
                                radius, Theme.Bg3);
            }

            // active top accent bar
            if (active)
            {
                var accent = new Rectangle(r.X + Theme.S(8), r.Y + Theme.S(2),
                                           r.Width - Theme.S(16), Math.Max(1, Theme.S(2)));
                Theme.FillRound(g, accent, Theme.S(1), Theme.Accent);
            }

            TabInfo info = _tabs[index];

            // protocol icon
            Color pc = Theme.ProtocolColor(info.Protocol);
            var iconRect = new Rectangle(r.X + Theme.S(10),
                r.Y + (r.Height - Theme.S(14)) / 2, Theme.S(14), Theme.S(14));
            DrawProtocolIcon(g, iconRect, info.Protocol, active ? pc : Theme.TextTertiary);

            // title
            Color textColor = active ? Theme.TextPrimary : Theme.TextSecondary;
            var font = active ? UiFonts.UI(12.5f, FontStyle.Bold) : UiFonts.UI(12.5f, FontStyle.Regular);
            int textX = iconRect.Right + Theme.S(6);
            int closeLeft = _closeRects[index].Left;
            var textRect = new Rectangle(textX, r.Y, Math.Max(0, closeLeft - Theme.S(4) - textX), r.Height);
            Theme.DrawText(g, info.Title ?? "", font, textColor, textRect,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left |
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix |
                TextFormatFlags.EndEllipsis);

            // close button
            var cr = _closeRects[index];
            bool closeHover = hover && _hoverKind == Kind.Close && _hoverTab == index;
            if (closeHover)
                Theme.FillRound(g, cr, Theme.S(4), Color.FromArgb(40, Theme.Danger));
            Theme.DrawIcon(g, "close", cr, closeHover ? Theme.Danger
                             : (active ? Theme.TextSecondary : Theme.TextTertiary));
        }

        private static GraphicsPath TopRoundRect(Rectangle r, int radius)
        {
            var path = new GraphicsPath();
            int d = Math.Max(0, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
            if (d <= 0) { path.AddRectangle(r); return path; }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddLine(r.Right, r.Y + d, r.Right, r.Bottom);
            path.AddLine(r.Right, r.Bottom, r.X, r.Bottom);
            path.AddLine(r.X, r.Bottom, r.X, r.Y + d);
            path.CloseFigure();
            return path;
        }

        private void DrawProtocolIcon(Graphics g, Rectangle r, ProtocolKind kind, Color c)
        {
            if (kind == ProtocolKind.Serial)
            {
                using (var pen = new Pen(c, Math.Max(1f, Theme.SF(1.5f))))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    var body = new Rectangle(r.X + Theme.S(2), r.Y + Theme.S(5),
                                             r.Width - Theme.S(4), Theme.S(6));
                    g.DrawRectangle(pen, body);
                    g.DrawLine(pen, r.X + Theme.S(5), r.Y + Theme.S(2),
                               r.X + Theme.S(5), r.Y + Theme.S(5));
                    g.DrawLine(pen, r.X + Theme.S(9), r.Y + Theme.S(2),
                               r.X + Theme.S(9), r.Y + Theme.S(5));
                }
            }
            else
            {
                Theme.DrawIcon(g, "host", r, c);
            }
        }

        private void DrawPlus(Graphics g)
        {
            var r = _btnPlus;
            if (r.Width <= 0) return;
            bool hover = _hoverKind == Kind.Plus;
            if (hover) Theme.FillRound(g, r, Theme.S(6), Theme.Bg3);
            Theme.DrawIcon(g, "plus", new Rectangle(r.X, r.Y, r.Width, r.Height),
                           hover ? Theme.TextPrimary : Theme.TextSecondary);
        }

        private void DrawToolButton(Graphics g, Rectangle r, string icon, Kind kind)
        {
            if (r.Width <= 0) return;
            bool hover = _hoverKind == kind;
            bool pressed = _pressedKind == kind;
            Color bg = pressed ? Theme.AccentSoft : (hover ? Theme.Bg3 : Theme.Bg0);
            Theme.FillRound(g, r, Theme.S(4), bg);
            Theme.DrawRound(g, r, Theme.S(4), hover ? Theme.BorderStrong : Theme.Border);
            Color ic = hover ? Theme.TextPrimary : Theme.TextSecondary;
            var ir = new Rectangle(r.X + (r.Width - Theme.S(14)) / 2,
                                   r.Y + (r.Height - Theme.S(14)) / 2,
                                   Theme.S(14), Theme.S(14));
            if (icon == "grid")
                DrawGridIcon(g, ir, ic);
            else if (icon == "more")
                DrawMoreIcon(g, ir, ic);
            else
                Theme.DrawIcon(g, icon, ir, ic);
        }

        private static void DrawGridIcon(Graphics g, Rectangle r, Color c)
        {
            using (var pen = new Pen(c, Math.Max(1f, Theme.SF(1.4f))))
            {
                int w = Theme.S(5), h = Theme.S(5), gap = Theme.S(2);
                g.DrawRectangle(pen, r.X + Theme.S(1), r.Y + Theme.S(1), w, h);
                g.DrawRectangle(pen, r.X + Theme.S(1) + w + gap, r.Y + Theme.S(1), w, h);
                g.DrawRectangle(pen, r.X + Theme.S(1), r.Y + Theme.S(1) + h + gap, w, h);
                g.DrawRectangle(pen, r.X + Theme.S(1) + w + gap, r.Y + Theme.S(1) + h + gap, w, h);
            }
        }

        private static void DrawMoreIcon(Graphics g, Rectangle r, Color c)
        {
            using (var b = new SolidBrush(c))
            {
                int d = Math.Max(2, Theme.S(2));
                int cy = r.Y + r.Height / 2 - d / 2;
                int gap = Theme.S(3);
                int x = r.X + r.Width / 2 - (d * 3 + gap * 2) / 2;
                for (int i = 0; i < 3; i++)
                    g.FillEllipse(b, x + i * (d + gap), cy, d, d);
            }
        }

        // ------------------------------------------------------------------
        // Hit testing & input
        // ------------------------------------------------------------------

        private Kind HitTest(Point p, out int tabIndex)
        {
            tabIndex = -1;
            if (_btnMore.Contains(p)) return Kind.More;
            if (_btnLayout.Contains(p)) return Kind.Layout;
            if (_btnSplitH.Contains(p)) return Kind.SplitH;
            if (_btnSplitV.Contains(p)) return Kind.SplitV;
            if (_btnPlus.Contains(p)) return Kind.Plus;
            for (int i = 0; i < _tabRects.Count; i++)
            {
                if (!_tabRects[i].Contains(p)) continue;
                tabIndex = i;
                if (_closeRects[i].Contains(p)) return Kind.Close;
                return Kind.Tab;
            }
            return Kind.None;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int tabIndex;
            Kind kind = HitTest(e.Location, out tabIndex);
            bool changed = kind != _hoverKind || tabIndex != _hoverTab;
            _hoverKind = kind;
            _hoverTab = tabIndex;
            Cursor = (kind == Kind.None) ? Cursors.Default : Cursors.Hand;

            if (_dragTab >= 0 && (e.Button & MouseButtons.Left) != 0)
            {
                if (!_dragging &&
                    (Math.Abs(e.X - _dragStart.X) > Theme.S(4) ||
                     Math.Abs(e.Y - _dragStart.Y) > Theme.S(4)))
                {
                    _dragging = true;
                }
                if (_dragging)
                {
                    _dragInsert = InsertIndexAt(e.X);
                    changed = true;
                }
            }
            if (changed) Invalidate();
        }

        private int InsertIndexAt(int x)
        {
            for (int i = 0; i < _tabRects.Count; i++)
            {
                if (x < _tabRects[i].Left + _tabRects[i].Width / 2) return i;
            }
            return _tabRects.Count;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button == MouseButtons.Middle)
            {
                int idx;
                if (HitTest(e.Location, out idx) == Kind.Tab && TabCloseRequested != null)
                    TabCloseRequested(idx);
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            int tabIndex;
            Kind kind = HitTest(e.Location, out tabIndex);
            _pressedKind = kind;
            _pressedTab = tabIndex;
            if (kind == Kind.Tab)
            {
                if (TabSelected != null) TabSelected(tabIndex);
                if (tabIndex != _active) { _active = tabIndex; }
                _dragTab = tabIndex;
                _dragStart = e.Location;
                _dragging = false;
                _dragInsert = tabIndex;
            }
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left)
            {
                _pressedKind = Kind.None;
                return;
            }

            int tabIndex;
            Kind kind = HitTest(e.Location, out tabIndex);

            if (_dragging && _dragTab >= 0)
            {
                int from = _dragTab;
                int to = _dragInsert;
                if (to > from) to--;
                _dragging = false;
                _dragTab = -1;
                _dragInsert = -1;
                if (to >= 0 && to < _tabs.Count && to != from && TabMoved != null)
                    TabMoved(from, to);
                Invalidate();
                _pressedKind = Kind.None;
                return;
            }
            _dragTab = -1;

            if (kind == _pressedKind && tabIndex == _pressedTab)
            {
                switch (kind)
                {
                    case Kind.Close:
                        if (TabCloseRequested != null) TabCloseRequested(tabIndex);
                        break;
                    case Kind.Plus:
                        if (NewTabRequested != null) NewTabRequested();
                        break;
                    case Kind.SplitV:
                        if (SplitRequested != null) SplitRequested(true);
                        break;
                    case Kind.SplitH:
                        if (SplitRequested != null) SplitRequested(false);
                        break;
                    case Kind.Layout:
                        ShowLayoutMenu();
                        break;
                    case Kind.More:
                        if (MoreRequested != null) MoreRequested();
                        break;
                }
            }
            _pressedKind = Kind.None;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hoverKind = Kind.None;
            _hoverTab = -1;
            Cursor = Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int content = 0;
            int spacing = Theme.S(TabSpacing);
            for (int i = 0; i < _tabRects.Count; i++) content += _tabRects[i].Width + spacing;
            int avail = Math.Max(0, _labelSplits.X - Theme.S(40));
            if (content <= avail) return;
            _scroll = Math.Max(0, Math.Min(Math.Max(0, content - avail), _scroll - e.Delta / 2));
            RecalcLayout();
            Invalidate();
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            Point p = PointToClient(Cursor.Position);
            int idx;
            if (HitTest(p, out idx) == Kind.None && NewTabRequested != null)
                NewTabRequested();
        }

        private void ShowLayoutMenu()
        {
            if (LayoutPresetRequested == null) return;
            ContextMenuStrip menu = DarkMenu.Create();
            var items = new[]
            {
                new { Text = "单窗格", Preset = LayoutPreset.Single },
                new { Text = "左右分屏 1×2", Preset = LayoutPreset.TwoColumns },
                new { Text = "上下分屏 2×1", Preset = LayoutPreset.TwoRows },
                new { Text = "三列 1×3", Preset = LayoutPreset.ThreeColumns },
                new { Text = "三行 3×1", Preset = LayoutPreset.ThreeRows },
                new { Text = "四窗格 2×2", Preset = LayoutPreset.Quad },
            };
            foreach (var it in items)
            {
                var preset = it.Preset;
                menu.Items.Add(DarkMenu.Item(it.Text, (s, ev) =>
                {
                    var handler = LayoutPresetRequested;
                    if (handler != null) handler(preset);
                }));
            }
            menu.Show(this, new Point(_btnLayout.Left, _btnLayout.Bottom + Theme.S(2)));
        }
    }
}
