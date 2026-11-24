using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace XPutty
{
    /// <summary>
    /// 44px macro toolbar.  Shows every enabled macro (unlimited — the strip
    /// scrolls horizontally with the wheel or the pager buttons), a dashed
    /// "+ 配置宏" button at the end, and a right-side macro-manager entry.
    /// </summary>
    public class MacroBar : Control, Theme.IThemeAware
    {
        private const int BtnH = 28;
        private const int StartX = 52;
        private const int Gap = 7;

        private List<MacroDef> _macros = new List<MacroDef>();
        private readonly List<Rectangle> _rects = new List<Rectangle>();
        private readonly List<MacroDef> _rectMacros = new List<MacroDef>();
        private Rectangle _configRect;
        private Rectangle _prevRect, _nextRect;
        private int _offset;
        private int _hover = -1;
        private int _pressed = -1;
        private bool _hoverPrev, _hoverNext, _hoverConfig, _hoverManager;

        public event Action<MacroDef> MacroClicked;
        public event Action ConfigureRequested;
        public event Action<MacroDef> MacroContextRequested;

        public MacroBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg1;
            Size = new Size(Theme.S(1000), Theme.S(44));
            Font = UiKit.UI(12f);
        }

        public void ApplyThemeColors()
        {
            BackColor = Theme.Bg1;
            Invalidate();
        }

        // ---- public API (SHELL_SPEC §8) --------------------------------
        public void SetMacros(List<MacroDef> macros)
        {
            _macros = new List<MacroDef>();
            if (macros != null)
            {
                foreach (var m in macros) if (m != null && m.Enabled) _macros.Add(m);
                _macros.Sort(delegate(MacroDef a, MacroDef b) { return a.Sort.CompareTo(b.Sort); });
            }
            _offset = Math.Max(0, Math.Min(_offset, MaxOffset()));
            LayoutButtons();
            Invalidate();
        }

        // ---- metrics ---------------------------------------------------
        private int StartXpx { get { return Theme.S(StartX); } }
        private int Gappx { get { return Theme.S(Gap); } }
        private int BtnHpx { get { return Theme.S(BtnH); } }

        private int HintWidth
        {
            get
            {
                if (ClientSize.Width < Theme.S(820)) return 0;
                return UiKit.Measure("Ctrl+Shift+1~8 触发", UiKit.UI(11.5f));
            }
        }

        private int ManagerWidth
        {
            get { return Theme.S(14 + 6) + UiKit.Measure("宏管理", UiKit.UI(11.5f)); }
        }

        private int RightAreaWidth
        {
            get
            {
                int w = Theme.S(12) + ManagerWidth;
                if (HintWidth > 0) w += HintWidth + Theme.S(20) + Theme.S(10);
                return w;
            }
        }

        private int BaseViewportWidth
        {
            get { return Math.Max(Theme.S(40), ClientSize.Width - RightAreaWidth - StartXpx); }
        }

        private int ContentWidth()
        {
            int w = 0;
            foreach (var m in _macros) w += ButtonWidth(m) + Gappx;
            w += ConfigWidth() + Gappx;
            return w;
        }

        private bool Overflow { get { return ContentWidth() > BaseViewportWidth; } }

        private int Gutter { get { return Overflow ? Theme.S(22) : 0; } }

        private int ViewportLeft { get { return StartXpx + Gutter; } }

        private int ViewportRight { get { return ClientSize.Width - RightAreaWidth - Gutter; } }

        private int ViewportWidth { get { return Math.Max(Theme.S(30), ViewportRight - ViewportLeft); } }

        private int MaxOffset()
        {
            return Math.Max(0, ContentWidth() - ViewportWidth);
        }

        private int ButtonWidth(MacroDef m)
        {
            int textW = UiKit.Measure(m.Name, UiKit.UI(12f));
            return Math.Max(Theme.S(52), Theme.S(32) + textW);
        }

        private int ConfigWidth()
        {
            return Theme.S(10 + 14 + 6 + 10) + UiKit.Measure("配置宏", UiKit.UI(12f));
        }

        private void LayoutButtons()
        {
            _rects.Clear();
            _rectMacros.Clear();
            int x = ViewportLeft - _offset;
            int y = (ClientSize.Height - BtnHpx) / 2;
            foreach (var m in _macros)
            {
                int w = ButtonWidth(m);
                _rects.Add(new Rectangle(x, y, w, BtnHpx));
                _rectMacros.Add(m);
                x += w + Gappx;
            }
            _configRect = new Rectangle(x, y, ConfigWidth(), BtnHpx);

            int ay = (ClientSize.Height - Theme.S(22)) / 2;
            _prevRect = new Rectangle(StartXpx, ay, Theme.S(20), Theme.S(22));
            _nextRect = new Rectangle(ViewportRight, ay, Theme.S(20), Theme.S(22));
            ClampOffset();
        }

        private void ClampOffset()
        {
            _offset = Math.Max(0, Math.Min(_offset, MaxOffset()));
        }

        // ---- paint ------------------------------------------------------
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.Bg1);
            UiKit.LineH(g, 0, ClientSize.Width, 0, Theme.BorderSoft);

            int cy = ClientSize.Height / 2;
            UiKit.Icon(g, "keyboard", new Rectangle(Theme.S(14), cy - Theme.S(6), Theme.S(12), Theme.S(12)),
                Theme.TextTertiary);
            UiKit.Text(g, "宏", UiKit.UIBold(10.5f), Theme.Blend(Theme.TextSecondary, Theme.TextTertiary, 0.6f),
                new Rectangle(Theme.S(32), 0, Theme.S(20), ClientSize.Height), UiKit.L);

            LayoutButtons();
            var oldClip = g.Clip;
            g.SetClip(new Rectangle(ViewportLeft, 0, ViewportWidth, ClientSize.Height));
            for (int i = 0; i < _rects.Count; i++)
            {
                var r = _rects[i];
                if (r.Right < ViewportLeft - Theme.S(4) || r.X > ViewportRight + Theme.S(4)) continue;
                PaintMacroButton(g, r, _rectMacros[i], i == _hover);
            }
            PaintConfigButton(g, _configRect, _hoverConfig);
            g.Clip = oldClip;

            if (Overflow)
            {
                PaintArrow(g, _prevRect, false, _offset > 0, _hoverPrev);
                PaintArrow(g, _nextRect, true, _offset < MaxOffset(), _hoverNext);
            }

            PaintRight(g);
        }

        private void PaintMacroButton(Graphics g, Rectangle r, MacroDef m, bool hover)
        {
            bool pressed = _pressed >= 0 && _pressed < _rectMacros.Count && _rectMacros[_pressed] == m;
            Color bg = hover ? UiKit.MacroBtnHover : UiKit.MacroBtnBg;
            if (pressed) bg = Theme.Blend(UiKit.MacroBtnBg, Theme.TermBg, 0.55f);
            Theme.FillRound(g, r, Theme.S(6), bg);
            Theme.DrawRound(g, new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), Theme.S(6),
                hover || pressed ? UiKit.MacroBtnHoverBorder : UiKit.MacroBtnBorder, Math.Max(1f, Theme.SF(1f)));
            UiKit.Dot(g, new Point(r.X + Theme.S(13), r.Y + r.Height / 2), Theme.S(6),
                UiKit.TagColor(m.Color));
            Color fg = hover ? Theme.TextPrimary : Theme.TextSecondary;
            if (pressed) fg = Theme.TextSecondary;
            var tr = new Rectangle(r.X + Theme.S(23), r.Y, r.Width - Theme.S(31), r.Height);
            if (r.X >= ViewportLeft && r.Right <= ViewportRight)
                UiKit.Text(g, m.Name, UiKit.UI(12f), fg, tr, UiKit.LE);
            else
                UiKit.TextClip(g, m.Name, UiKit.UI(12f), fg, tr);
        }

        private void PaintConfigButton(Graphics g, Rectangle r, bool hover)
        {
            if (hover) Theme.FillRound(g, r, Theme.S(6), UiKit.HoverRow);
            using (var path = Theme.RoundRect(new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), Theme.S(6)))
            using (var pen = new Pen(UiKit.DashedBorder, Math.Max(1f, Theme.SF(1f))))
            {
                pen.DashStyle = DashStyle.Dash;
                pen.DashPattern = new[] { 3f, 2.5f };
                g.DrawPath(pen, path);
            }
            UiKit.Icon(g, "plus", new Rectangle(r.X + Theme.S(10), r.Y + (r.Height - Theme.S(14)) / 2,
                Theme.S(14), Theme.S(14)), UiKit.DashedText);
            var tr = new Rectangle(r.X + Theme.S(30), r.Y, r.Width - Theme.S(36), r.Height);
            if (r.X >= ViewportLeft && r.Right <= ViewportRight)
                UiKit.Text(g, "配置宏", UiKit.UI(12f), UiKit.DashedText, tr, UiKit.L);
            else
                UiKit.TextClip(g, "配置宏", UiKit.UI(12f), UiKit.DashedText, tr);
        }

        private void PaintArrow(Graphics g, Rectangle r, bool right, bool enabled, bool hover)
        {
            Theme.FillRound(g, r, Theme.S(4), hover && enabled ? UiKit.MacroBtnHover : Theme.Bg1);
            Theme.DrawRound(g, new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), Theme.S(4),
                hover && enabled ? UiKit.MacroBtnHoverBorder : Theme.BorderSoft, Math.Max(1f, Theme.SF(1f)));
            var cr = new Rectangle(r.X + (r.Width - Theme.S(12)) / 2, r.Y + (r.Height - Theme.S(12)) / 2,
                Theme.S(12), Theme.S(12));
            UiKit.Chevron(g, cr, right ? 1 : 3, enabled ? Theme.TextSecondary : Theme.TextDisabled);
        }

        private void PaintRight(Graphics g)
        {
            int right = ClientSize.Width - Theme.S(12);
            Font f = UiKit.UI(11.5f);
            int mgrW = ManagerWidth;
            int mgrX = right - mgrW;
            int cy = ClientSize.Height / 2;

            if (HintWidth > 0)
            {
                string hint = "Ctrl+Shift+1~8 触发";
                int hintX = mgrX - Theme.S(20) - HintWidth;
                UiKit.Text(g, hint, f, Theme.TextTertiary,
                    new Rectangle(hintX, 0, HintWidth, ClientSize.Height), UiKit.L);
                UiKit.LineV(g, hintX - Theme.S(10), cy - Theme.S(8), cy + Theme.S(8), Theme.Border);
            }

            if (_hoverManager)
                Theme.FillRound(g, new Rectangle(mgrX - Theme.S(6), cy - Theme.S(11),
                    mgrW + Theme.S(10), Theme.S(22)), Theme.S(4), UiKit.HoverRow);
            UiKit.Icon(g, "sliders", new Rectangle(mgrX, cy - Theme.S(7), Theme.S(14), Theme.S(14)),
                Theme.TextSecondary);
            UiKit.Text(g, "宏管理", f, Theme.TextSecondary,
                new Rectangle(mgrX + Theme.S(20), 0, UiKit.Measure("宏管理", f) + Theme.S(4), ClientSize.Height),
                UiKit.L);
        }

        private Rectangle ManagerRect()
        {
            int right = ClientSize.Width - Theme.S(12);
            int mgrW = ManagerWidth;
            return new Rectangle(right - mgrW - Theme.S(4), Theme.S(8), mgrW + Theme.S(8),
                ClientSize.Height - Theme.S(16));
        }

        // ---- interaction ------------------------------------------------
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int idx = HitMacro(e.Location);
            bool prev = Overflow && _prevRect.Contains(e.Location);
            bool next = Overflow && _nextRect.Contains(e.Location);
            bool cfg = _configRect.Contains(e.Location) && InViewport(e.X);
            bool mgr = ManagerRect().Contains(e.Location);
            if (idx != _hover || prev != _hoverPrev || next != _hoverNext
                || cfg != _hoverConfig || mgr != _hoverManager)
            {
                _hover = idx;
                _hoverPrev = prev;
                _hoverNext = next;
                _hoverConfig = cfg;
                _hoverManager = mgr;
                Invalidate();
            }
            Cursor = (idx >= 0 || prev || next || cfg || mgr) ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hover = -1;
            _hoverPrev = _hoverNext = _hoverConfig = _hoverManager = false;
            Cursor = Cursors.Default;
            Invalidate();
        }

        private bool InViewport(int x)
        {
            return x >= ViewportLeft && x <= ViewportRight;
        }

        private int HitMacro(Point p)
        {
            if (!InViewport(p.X)) return -1;
            for (int i = 0; i < _rects.Count; i++)
                if (_rects[i].Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            _pressed = -1;
            if (e.Button != MouseButtons.Left) return;

            if (Overflow && _prevRect.Contains(e.Location)) { Scroll(-1); return; }
            if (Overflow && _nextRect.Contains(e.Location)) { Scroll(1); return; }

            int idx = HitMacro(e.Location);
            if (idx >= 0)
            {
                _pressed = idx;
                Invalidate();
                return;
            }
            if (_configRect.Contains(e.Location) && InViewport(e.X))
            {
                var h = ConfigureRequested;
                if (h != null) h();
                return;
            }
            if (ManagerRect().Contains(e.Location))
            {
                var h = ConfigureRequested;
                if (h != null) h();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            int pressedIndex = _pressed;
            _pressed = -1;
            if (e.Button == MouseButtons.Right)
            {
                int ridx = HitMacro(e.Location);
                if (ridx >= 0) ShowMacroMenu(_rectMacros[ridx], e.Location);
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            Invalidate();
            if (pressedIndex < 0) return;
            int over = HitMacro(e.Location);
            if (over >= 0 && over == pressedIndex && over < _rectMacros.Count)
            {
                var m = _rectMacros[over];
                var h = MacroClicked;
                if (h != null) h(m);
            }
        }

        private void ShowMacroMenu(MacroDef m, Point p)
        {
            if (MacroContextRequested != null)
            {
                MacroContextRequested(m);
                return;
            }
            var menu = DarkMenu.Create();
            menu.Items.Add(DarkMenu.Item("编辑宏…", delegate
            {
                var h = ConfigureRequested;
                if (h != null) h();
            }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(DarkMenu.Item("禁用该宏", delegate
            {
                m.Enabled = false;
                AppContext.I.SaveAll();
                if (AppContext.I.Main != null) AppContext.I.Main.RefreshMacros();
            }));
            menu.Items.Add(DarkMenu.Item("删除宏", delegate
            {
                AppContext.I.DeleteMacro(m.Id);
                AppContext.I.SaveAll();
                if (AppContext.I.Main != null) AppContext.I.Main.RefreshMacros();
            }, null, true));
            menu.Show(this, p);
        }

        private void Scroll(int dir)
        {
            _offset = Math.Max(0, Math.Min(MaxOffset(), _offset + dir * Theme.S(140)));
            LayoutButtons();
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!Overflow) return;
            _offset = Math.Max(0, Math.Min(MaxOffset(), _offset - Math.Sign(e.Delta) * Theme.S(90)));
            LayoutButtons();
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutButtons();
        }
    }
}
