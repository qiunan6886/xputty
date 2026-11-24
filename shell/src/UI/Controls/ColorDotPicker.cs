using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace XPutty
{
    /// <summary>Row of six tag colour dots, used by macro and session editors.</summary>
    public class ColorDotPicker : Control
    {
        private string _selected = "blue";
        private int _hover = -1;

        public event EventHandler Changed;

        public ColorDotPicker()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                     | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
        }

        public string SelectedColor
        {
            get { return _selected; }
            set { _selected = value ?? ""; Invalidate(); }
        }

        private int Dia { get { return Theme.S(18); } }
        private int Gap { get { return Theme.S(10); } }

        private Rectangle DotRect(int i)
        {
            int dia = Dia;
            return new Rectangle(i * (dia + Gap), (Height - dia) / 2, dia, dia);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = -1;
            for (int i = 0; i < UiKit.MacroTags.Length; i++)
                if (DotRect(i).Contains(e.Location)) { h = i; break; }
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
            for (int i = 0; i < UiKit.MacroTags.Length; i++)
            {
                if (!DotRect(i).Contains(e.Location)) continue;
                _selected = UiKit.MacroTags[i];
                Invalidate();
                var h = Changed;
                if (h != null) h(this, EventArgs.Empty);
                return;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            for (int i = 0; i < UiKit.MacroTags.Length; i++)
            {
                var r = DotRect(i);
                Color c = UiKit.TagColor(UiKit.MacroTags[i]);
                bool sel = string.Equals(_selected, UiKit.MacroTags[i], StringComparison.OrdinalIgnoreCase);
                if (sel)
                {
                    var ring = new Rectangle(r.X - Theme.S(4), r.Y - Theme.S(4),
                                             r.Width + Theme.S(8), r.Height + Theme.S(8));
                    using (var pen = new Pen(c, Math.Max(1f, Theme.SF(1.5f))))
                        g.DrawEllipse(pen, ring);
                }
                else if (i == _hover)
                {
                    using (var b = new SolidBrush(Color.FromArgb(40, c)))
                        g.FillEllipse(b, new Rectangle(r.X - Theme.S(2), r.Y - Theme.S(2),
                                                       r.Width + Theme.S(4), r.Height + Theme.S(4)));
                }
                using (var b = new SolidBrush(c))
                    g.FillEllipse(b, r);
            }
        }
    }
}
