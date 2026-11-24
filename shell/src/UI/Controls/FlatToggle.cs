using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace XPutty
{
    /// <summary>34x18 pill switch with a 120ms colour/position animation.</summary>
    public class FlatToggle : Control
    {
        private bool _checked;
        private float _t;              // 0 = off, 1 = on
        private readonly Timer _timer;
        private bool _hover;

        public event EventHandler CheckedChanged;

        public FlatToggle()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                     | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Size = new Size(Theme.S(34), Theme.S(18));

            _timer = new Timer();
            _timer.Interval = 15;
            _timer.Tick += delegate { Step(); };
        }

        public bool Checked
        {
            get { return _checked; }
            set
            {
                if (_checked == value) return;
                _checked = value;
                _timer.Start();
                var h = CheckedChanged;
                if (h != null) h(this, EventArgs.Empty);
                Invalidate();
            }
        }

        /// <summary>Set without animation / events (used while loading a form).</summary>
        public void SetCheckedSilent(bool value)
        {
            _checked = value;
            _t = value ? 1f : 0f;
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _timer.Dispose();
            base.Dispose(disposing);
        }

        private void Step()
        {
            float target = _checked ? 1f : 0f;
            float d = target - _t;
            if (Math.Abs(d) < 0.08f)
            {
                _t = target;
                _timer.Stop();
            }
            else
            {
                _t += d * 0.35f;
            }
            Invalidate();
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
            if (e.Button == MouseButtons.Left && Enabled) Checked = !Checked;
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
            Invalidate();
            base.OnEnabledChanged(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width, Height);
            int rad = r.Height / 2;

            Color off = Enabled ? UiKit.ToggleOff : Theme.Blend(Theme.Bg2, UiKit.ToggleOff, 0.45f);
            Color on = Enabled ? Theme.Accent : Theme.Blend(Theme.Bg2, Theme.Accent, 0.35f);
            if (_hover && Enabled)
            {
                off = Theme.Blend(off, Theme.TextTertiary, 0.25f);
                on = Theme.Blend(on, Color.White, 0.1f);
            }
            Color track = Theme.Blend(off, on, _t);
            Theme.FillRound(g, r, rad, track);

            int knob = r.Height - Theme.S(4);
            int x = (int)Math.Round(Theme.S(2) + (r.Width - knob - Theme.S(4)) * _t);
            var kr = new Rectangle(x, Theme.S(2), knob, knob);
            using (var b = new SolidBrush(Color.White))
                g.FillEllipse(b, kr);
        }
    }
}
