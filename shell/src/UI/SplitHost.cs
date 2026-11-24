using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace XPutty
{
    /// <summary>
    /// Hosts one tab's split tree.  Panes are absolutely positioned child
    /// controls; the 1px gaps between them show this control's background and
    /// double as the visual splitter line (the hit zone is inflated to 3px on
    /// each side and drags are forwarded here by PaneView).
    /// </summary>
    public class SplitHost : Control
    {
        public SplitTree Tree;
        public PaneView ActivePane;
        public PaneView MaximizedPane;

        public event Action<PaneView> ActivePaneChanged;

        private readonly List<SplitDivider> _dividers = new List<SplitDivider>();
        private SplitDivider _hoverDivider;
        private SplitDivider _dragDivider;
        private Point _dragStartScreen;
        private bool _dragging;

        public SplitHost()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw, true);
            BackColor = Theme.PaneSplit;
        }

        public List<SplitDivider> Dividers { get { return _dividers; } }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (Tree != null) Relayout();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (Tree != null) Relayout();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible || Disposing || IsDisposed) return;
            if (Tree != null)
            {
                Relayout();
                EnsurePanesStarted();
            }
        }

        /// <summary>A pane is safe to lay out only while it is alive.</summary>
        private static bool PaneAlive(PaneView pane)
        {
            return pane != null && !pane.IsDisposed && !pane.Disposing;
        }

        // ------------------------------------------------------------------
        // Tree wiring
        // ------------------------------------------------------------------

        public void Rebuild(SplitTree tree)
        {
            Tree = tree;
            if (Tree != null)
            {
                var panes = Tree.EnumeratePanes();
                for (int i = 0; i < panes.Count; i++) panes[i].Host = this;
            }
            Relayout();
            if (Tree != null && Tree.ActivePane != null && Tree.ActivePane.Parent == this)
                SetActivePane(Tree.ActivePane, false);
            else
            {
                var panes = Tree != null ? Tree.EnumeratePanes() : new List<PaneView>();
                if (panes.Count > 0) SetActivePane(panes[0], false);
            }
        }

        /// <summary>Position every pane according to the tree.</summary>
        public void Relayout()
        {
            if (Disposing || IsDisposed) return;
            try
            {
                RelayoutCore();
            }
            catch (ObjectDisposedException)
            {
                // A tab/pane can be torn down while a layout pass is in
                // flight (e.g. closing a tab disposes panes before the host's
                // WM_SHOWWINDOW/VisibleChanged callback runs).  The tab is
                // going away anyway, so swallow it instead of crashing.
            }
            catch (Exception ex)
            {
                Diag.Log("SplitHost.Relayout: " + ex);
            }
        }

        private void RelayoutCore()
        {
            if (Tree == null || Tree.Root == null)
            {
                for (int i = Controls.Count - 1; i >= 0; i--)
                {
                    if (Controls[i] is PaneView) Controls.RemoveAt(i);
                }
                _dividers.Clear();
                Invalidate();
                return;
            }

            if (MaximizedPane != null && (!PaneAlive(MaximizedPane) ||
                                           Tree.FindLeaf(MaximizedPane) == null))
                MaximizedPane = null;

            if (MaximizedPane != null)
            {
                _dividers.Clear();
                var all = Tree.EnumeratePanes();
                for (int i = 0; i < all.Count; i++)
                {
                    PaneView p = all[i];
                    if (!PaneAlive(p)) continue;
                    bool vis = p == MaximizedPane;
                    if (p.Parent != this) Controls.Add(p);
                    if (p.Bounds.Width != Width || p.Bounds.Height != Height)
                        p.SetBounds(0, 0, Math.Max(0, Width), Math.Max(0, Height));
                    if (p.Visible != vis) p.Visible = vis;
                }
                Invalidate();
                return;
            }

            if (Width <= 0 || Height <= 0) return;

            var placements = new List<PanePlacement>();
            var dividers = new List<SplitDivider>();
            Tree.Layout(new Rectangle(0, 0, Width, Height), Theme.S(1),
                        placements, dividers, Theme.S(2));

            var present = new HashSet<PaneView>();
            for (int i = 0; i < placements.Count; i++)
            {
                PanePlacement pl = placements[i];
                if (!PaneAlive(pl.Pane)) continue;
                present.Add(pl.Pane);
                if (pl.Pane.Parent != this) Controls.Add(pl.Pane);
                if (pl.Pane.Bounds != pl.Bounds) pl.Pane.SetBounds(pl.Bounds.X, pl.Bounds.Y,
                                                                   pl.Bounds.Width, pl.Bounds.Height);
                if (!pl.Pane.Visible) pl.Pane.Visible = true;
            }

            for (int i = Controls.Count - 1; i >= 0; i--)
            {
                var pane = Controls[i] as PaneView;
                if (pane != null && !present.Contains(pane)) Controls.RemoveAt(i);
            }

            _dividers.Clear();
            _dividers.AddRange(dividers);
            Invalidate();
        }

        public void SetActivePane(PaneView pane, bool raiseEvent)
        {
            if (Disposing || IsDisposed) return;
            if (pane != null && !PaneAlive(pane)) return;
            if (pane != null && Tree != null && Tree.FindLeaf(pane) == null) return;
            bool changed = ActivePane != pane;
            ActivePane = pane;
            if (Tree != null) Tree.ActivePane = pane;

            var panes = Tree != null ? Tree.EnumeratePanes() : new List<PaneView>();
            for (int i = 0; i < panes.Count; i++)
            {
                if (!PaneAlive(panes[i])) continue;
                bool active = panes[i] == pane;
                if (panes[i].IsActive != active)
                {
                    panes[i].IsActive = active;
                    panes[i].Invalidate();
                }
            }
            if (changed && raiseEvent && ActivePaneChanged != null)
                ActivePaneChanged(pane);
            Invalidate();
        }

        /// <summary>Make sure every pane of this tab has started its process.</summary>
        public void EnsurePanesStarted()
        {
            if (Tree == null) return;
            var panes = Tree.EnumeratePanes();
            for (int i = 0; i < panes.Count; i++)
            {
                if (!PaneAlive(panes[i])) continue;
                if (MaximizedPane != null && panes[i] != MaximizedPane) continue;
                panes[i].EnsureStarted();
            }
        }

        public void SetMaximized(PaneView pane)
        {
            MaximizedPane = pane;
            Relayout();
            if (pane != null) SetActivePane(pane, true);
        }

        public void ToggleMaximize(PaneView pane)
        {
            SetMaximized(MaximizedPane == pane ? null : pane);
        }

        // ------------------------------------------------------------------
        // Splitter hit testing / dragging
        // ------------------------------------------------------------------

        public SplitDivider HitDivider(Point clientPoint)
        {
            for (int i = 0; i < _dividers.Count; i++)
            {
                if (_dividers[i].HitBounds.Contains(clientPoint)) return _dividers[i];
            }
            return null;
        }

        public bool IsSplitterAt(Point screenPoint, out bool vertical)
        {
            vertical = true;
            var d = HitDivider(PointToClient(screenPoint));
            if (d == null) return false;
            vertical = d.Vertical;
            return true;
        }

        public bool TryBeginSplitterDrag(Point screenPoint)
        {
            if (_dragging) return true;
            var d = HitDivider(PointToClient(screenPoint));
            if (d == null) return false;
            _dragDivider = d;
            _dragStartScreen = screenPoint;
            _dragging = true;
            Capture = true;
            SetHoverDivider(d);
            Cursor = d.Vertical ? Cursors.SizeWE : Cursors.SizeNS;
            return true;
        }

        public void SetHoverDivider(SplitDivider d)
        {
            if (_hoverDivider == d) return;
            _hoverDivider = d;
            Invalidate();
        }

        public void ClearHover()
        {
            if (_hoverDivider == null) return;
            _hoverDivider = null;
            Invalidate();
        }

        public void ResetDividerAt(Point screenPoint)
        {
            var d = HitDivider(PointToClient(screenPoint));
            if (d == null || Tree == null) return;
            Tree.ResetDivider(d);
            Relayout();
        }

        private void RefreshDragDivider()
        {
            if (_dragDivider == null) return;
            for (int i = 0; i < _dividers.Count; i++)
            {
                if (_dividers[i].Node == _dragDivider.Node &&
                    _dividers[i].Index == _dragDivider.Index)
                {
                    _dragDivider = _dividers[i];
                    return;
                }
            }
            _dragDivider = null;
            _dragging = false;
            Capture = false;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left)
            {
                if (TryBeginSplitterDrag(PointToScreen(e.Location))) return;
                var d = HitDivider(e.Location);
                if (d != null) SetHoverDivider(d);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);

            if (_dragDivider != null && _dragging)
            {
                int offset = _dragDivider.Vertical
                    ? PointToClient(PointToScreen(e.Location)).X - _dragDivider.NodeBounds.X
                    : PointToClient(PointToScreen(e.Location)).Y - _dragDivider.NodeBounds.Y;
                int minSize = _dragDivider.Vertical ? Theme.S(160) : Theme.S(120);
                if (Tree != null)
                {
                    Tree.DragDivider(_dragDivider, offset, minSize);
                    Relayout();
                    RefreshDragDivider();
                }
                return;
            }

            var hover = HitDivider(e.Location);
            SetHoverDivider(hover);
            var d = hover;
            Cursor = d == null ? Cursors.Default
                   : d.Vertical ? Cursors.SizeWE : Cursors.SizeNS;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_dragDivider != null)
            {
                _dragDivider = null;
                _dragging = false;
                Capture = false;
                Cursor = Cursors.Default;
                Relayout();
                return;
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            ResetDividerAt(PointToScreen(e.Location));
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (!_dragging) ClearHover();
        }

        // ------------------------------------------------------------------
        // Painting
        // ------------------------------------------------------------------

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.Clear(Theme.PaneSplit);

            if (Tree == null || Tree.Root == null)
            {
                using (var font = UiFonts.UI(13f, FontStyle.Regular))
                using (var small = UiFonts.UI(11.5f, FontStyle.Regular))
                {
                    const string title = "没有打开的会话";
                    const string hint = "在左侧双击会话，或按 Ctrl+Shift+T 新建标签";
                    int tw = TextRenderer.MeasureText(title, font).Width;
                    int th = TextRenderer.MeasureText(title, font).Height;
                    int hw = TextRenderer.MeasureText(hint, small).Width;
                    int hh = TextRenderer.MeasureText(hint, small).Height;
                    int cx = Width / 2;
                    int cy = Height / 2;
                    Theme.DrawText(g, title, font, Theme.TextSecondary,
                        new Rectangle(cx - tw / 2, cy - th - Theme.S(4), tw, th),
                        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                    Theme.DrawText(g, hint, small, Theme.TextTertiary,
                        new Rectangle(cx - hw / 2, cy + Theme.S(4), hw, hh),
                        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                }
                return;
            }

            var highlight = _dragDivider ?? _hoverDivider;
            if (highlight != null)
            {
                using (var b = new SolidBrush(Theme.FocusLine))
                    g.FillRectangle(b, highlight.Bounds);
            }
        }
    }
}
