using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace XPutty
{
    public enum ChromeStyle
    {
        /// <summary>Transparent background, text secondary, hover tint.</summary>
        Ghost,
        /// <summary>bg2 fill + 1px border, text secondary (secondary dialog button).</summary>
        Flat,
        /// <summary>Filled accent gradient, white text (primary action).</summary>
        Primary,
        /// <summary>Transparent + 1px border, text secondary.</summary>
        Outline,
        /// <summary>Transparent + dashed accent-ish border, accent text.</summary>
        Dashed,
        /// <summary>Danger text, red tint hover (delete).</summary>
        Danger,
    }

    /// <summary>
    /// Flat self-drawn button: optional line icon, optional right-hand key cap,
    /// hover / pressed / disabled states, accent (primary) variant.
    /// </summary>
    public class ChromeButton : Control
    {
        private bool _hover, _pressed;

        public ChromeStyle Style = ChromeStyle.Flat;
        public string Icon;
        public string RightHint;
        public string SubText;                    // second line, smaller (list-row buttons)
        public int Radius = 6;
        public int PadX = 10;
        public bool IconOnly;
        public bool AutoEllipsis = true;
        /// <summary>Optional icon tint (e.g. Theme.Danger for a record dot).</summary>
        public Color IconTint = Color.Empty;

        public ChromeButton()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                     | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            Cursor = Cursors.Hand;
            Font = UiKit.UI(12.5f);
            DoubleBuffered = true;
        }

        public override string Text
        {
            get { return base.Text; }
            set { base.Text = value; Invalidate(); }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            _hover = true; Invalidate(); base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); }
            base.OnMouseDown(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { _pressed = false; Invalidate(); }
            base.OnMouseUp(e);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            Cursor = Enabled ? Cursors.Hand : Cursors.Default;
            Invalidate();
            base.OnEnabledChanged(e);
        }

        protected override void OnTextChanged(EventArgs e)
        {
            Invalidate();
            base.OnTextChanged(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle r = new Rectangle(0, 0, Width, Height);
            int rad = Theme.S(Radius);

            Color textColor = Theme.TextSecondary;
            Color iconColor = Theme.TextSecondary;

            if (!Enabled)
            {
                textColor = Theme.TextDisabled;
                iconColor = Theme.TextDisabled;
            }

            switch (Style)
            {
                case ChromeStyle.Primary:
                {
                    Color top = Theme.Blend(Theme.Accent, Color.White, 0.12f);
                    Color bottom = Theme.Blend(Theme.Accent, Theme.AccentPressed, 0.35f);
                    if (!Enabled)
                    {
                        top = Theme.Blend(Theme.Bg3, Theme.Accent, 0.18f);
                        bottom = top;
                    }
                    else if (_pressed)
                    {
                        top = Theme.AccentPressed;
                        bottom = Theme.Blend(Theme.AccentPressed, Color.Black, 0.12f);
                    }
                    else if (_hover)
                    {
                        top = Theme.Blend(Theme.AccentHover, Color.White, 0.06f);
                        bottom = Theme.AccentHover;
                    }
                    using (var path = Theme.RoundRect(r, rad))
                    using (var b = new LinearGradientBrush(r, top, bottom, LinearGradientMode.Vertical))
                        g.FillPath(b, path);
                    Color edge = Enabled ? Theme.Blend(Theme.Accent, Color.Black, 0.28f) : Theme.Border;
                    Theme.DrawRound(g, new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), rad, edge, 1f);
                    textColor = Enabled ? Color.White : Theme.TextDisabled;
                    iconColor = textColor;
                    break;
                }
                case ChromeStyle.Flat:
                {
                    Color bg = Theme.Bg2, border = Theme.Border;
                    if (!Enabled) { bg = Theme.Bg2; border = Theme.BorderSoft; }
                    else if (_pressed) { bg = Theme.Bg3; border = Theme.BorderStrong; }
                    else if (_hover) { bg = Theme.Blend(Theme.Bg2, Theme.Bg3, 0.9f); border = Theme.BorderStrong; }
                    Theme.FillRound(g, r, rad, bg);
                    Theme.DrawRound(g, new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), rad, border, 1f);
                    textColor = Enabled ? Theme.TextPrimary : Theme.TextDisabled;
                    iconColor = textColor;
                    break;
                }
                case ChromeStyle.Outline:
                {
                    if (_hover && Enabled)
                        Theme.FillRound(g, r, rad, _pressed ? Theme.Bg3 : UiKit.HoverRow);
                    Theme.DrawRound(g, new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), rad,
                        _hover && Enabled ? Theme.BorderStrong : Theme.Border, 1f);
                    textColor = Enabled ? Theme.TextPrimary : Theme.TextDisabled;
                    iconColor = Theme.TextSecondary;
                    break;
                }
                case ChromeStyle.Dashed:
                {
                    if (_hover && Enabled)
                        Theme.FillRound(g, r, rad, UiKit.HoverRow);
                    using (var path = Theme.RoundRect(new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), rad))
                    using (var pen = new Pen(UiKit.DashedBorder, Math.Max(1f, Theme.SF(1f))))
                    {
                        pen.DashStyle = DashStyle.Dash;
                        pen.DashPattern = new[] { 3f, 2.5f };
                        g.DrawPath(pen, path);
                    }
                    textColor = Enabled ? UiKit.DashedText : Theme.TextDisabled;
                    iconColor = textColor;
                    break;
                }
                case ChromeStyle.Danger:
                {
                    if (_hover && Enabled)
                    {
                        using (var b = new SolidBrush(Color.FromArgb(_pressed ? 44 : 30, Theme.Danger)))
                        using (var path = Theme.RoundRect(r, rad))
                            g.FillPath(b, path);
                    }
                    textColor = Enabled ? Theme.Danger : Theme.TextDisabled;
                    iconColor = textColor;
                    break;
                }
                default: // Ghost
                {
                    if (_hover && Enabled)
                    {
                        using (var b = new SolidBrush(Color.FromArgb(_pressed ? 26 : 16, 255, 255, 255)))
                        using (var path = Theme.RoundRect(r, rad))
                            g.FillPath(b, path);
                    }
                    textColor = Enabled ? Theme.TextPrimary : Theme.TextDisabled;
                    iconColor = Enabled ? Theme.TextSecondary : Theme.TextDisabled;
                    break;
                }
            }

            int iconSize = Theme.S(14);
            if (IconTint != Color.Empty && Enabled && Style != ChromeStyle.Primary)
                iconColor = IconTint;
            int cy = r.Y + r.Height / 2;
            int x = r.X + Theme.S(PadX);
            if (!string.IsNullOrEmpty(Icon) && !IconOnly)
            {
                var ir = new Rectangle(x, cy - iconSize / 2, iconSize, iconSize);
                UiKit.Icon(g, Icon, ir, iconColor);
                x += iconSize + Theme.S(6);
            }

            int rightEdge = r.Right - Theme.S(PadX);
            Rectangle capRect = Rectangle.Empty;
            if (!string.IsNullOrEmpty(RightHint))
            {
                Font capFont = UiKit.Mono(9.5f);
                capRect = UiKit.KeyCapRect(g, rightEdge, cy, RightHint, capFont);
                Color capFg = Style == ChromeStyle.Primary
                    ? Color.FromArgb(220, 255, 255, 255)
                    : Theme.TextTertiary;
                Color capBorder = Style == ChromeStyle.Primary
                    ? Color.FromArgb(90, 255, 255, 255)
                    : UiKit.KeyCapBorder;
                UiKit.KeyCap(g, capRect, RightHint, capFont, capFg, capBorder);
                rightEdge = capRect.X - Theme.S(6);
            }

            if (IconOnly && !string.IsNullOrEmpty(Icon))
            {
                var ir = new Rectangle(r.X + (r.Width - iconSize) / 2, cy - iconSize / 2, iconSize, iconSize);
                UiKit.Icon(g, Icon, ir, iconColor);
                return;
            }

            if (!string.IsNullOrEmpty(Text))
            {
                var tr = new Rectangle(x, r.Y, Math.Max(0, rightEdge - x), r.Height);
                Font f = Font;
                if (tr.Width < UiKit.Measure(Text, f) && AutoEllipsis)
                    UiKit.Text(g, Text, f, textColor, tr, UiKit.LE);
                else
                    UiKit.Text(g, Text, f, textColor, tr, UiKit.L);
            }
        }
    }
}
