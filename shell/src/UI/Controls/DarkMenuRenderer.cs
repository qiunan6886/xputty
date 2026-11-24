using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace XPutty
{
    /// <summary>
    /// Dark renderer for ContextMenuStrip / ToolStripDropDown so right-click
    /// menus do not pop up with the default light Windows chrome.
    /// </summary>
    public class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkColorTable())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            e.Graphics.Clear(UiKit.DialogBg);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            Rectangle r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            Theme.DrawRound(e.Graphics, r, Theme.S(8), Theme.BorderStrong, Math.Max(1f, Theme.SF(1f)));
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            var g = e.Graphics;
            var item = e.Item;
            if (!item.Selected && !item.Pressed) return;
            Rectangle r = new Rectangle(Theme.S(4), 0, item.Width - Theme.S(8), item.Height);
            Theme.FillRound(g, r, Theme.S(4),
                item.Pressed ? Theme.Bg3 : UiKit.HoverRow);
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.TextPrimary : Theme.TextDisabled;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            Rectangle r = e.Item.Bounds;
            int y = r.Y + r.Height / 2;
            UiKit.LineH(e.Graphics, Theme.S(8), r.Right - Theme.S(8), y, Theme.Border);
        }

        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            var g = e.Graphics;
            var r = new Rectangle(e.ImageRectangle.X + Theme.S(2), e.ImageRectangle.Y + Theme.S(2),
                                  e.ImageRectangle.Width - Theme.S(4), e.ImageRectangle.Height - Theme.S(4));
            UiKit.Check(g, r, Theme.Accent);
        }

        private class DarkColorTable : ProfessionalColorTable
        {
            public override Color MenuBorder { get { return Theme.BorderStrong; } }
            public override Color MenuItemBorder { get { return UiKit.HoverRow; } }
            public override Color MenuItemSelected { get { return UiKit.HoverRow; } }
            public override Color MenuItemSelectedGradientBegin { get { return UiKit.HoverRow; } }
            public override Color MenuItemSelectedGradientEnd { get { return UiKit.HoverRow; } }
            public override Color MenuItemPressedGradientBegin { get { return Theme.Bg3; } }
            public override Color MenuItemPressedGradientEnd { get { return Theme.Bg3; } }
            public override Color ToolStripDropDownBackground { get { return UiKit.DialogBg; } }
            public override Color ImageMarginGradientBegin { get { return UiKit.DialogBg; } }
            public override Color ImageMarginGradientMiddle { get { return UiKit.DialogBg; } }
            public override Color ImageMarginGradientEnd { get { return UiKit.DialogBg; } }
            public override Color SeparatorDark { get { return Theme.Border; } }
            public override Color SeparatorLight { get { return Theme.Border; } }
        }
    }

    public static class DarkMenu
    {
        public static ContextMenuStrip Create()
        {
            var m = new ContextMenuStrip();
            m.Renderer = new DarkMenuRenderer();
            m.BackColor = UiKit.DialogBg;
            m.ForeColor = Theme.TextPrimary;
            m.Font = UiKit.UI(12f);
            m.ShowImageMargin = false;
            m.ShowCheckMargin = true;
            m.Padding = new Padding(Theme.S(2), Theme.S(4), Theme.S(2), Theme.S(4));
            m.MinimumSize = new Size(Theme.S(150), 0);
            return m;
        }

        public static ToolStripMenuItem Item(string text, EventHandler onClick = null,
                                             string icon = null, bool danger = false)
        {
            var it = new ToolStripMenuItem(text);
            it.Font = UiKit.UI(12f);
            it.ForeColor = danger ? Theme.Danger : Theme.TextPrimary;
            it.Padding = new Padding(0, Theme.S(3), 0, Theme.S(3));
            if (onClick != null) it.Click += onClick;
            return it;
        }
    }
}
