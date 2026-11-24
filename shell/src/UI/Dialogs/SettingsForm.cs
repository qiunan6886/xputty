using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace XPutty
{
    /// <summary>Settings dialog (620x460): theme, terminal font, chrome visibility,
    /// close behaviour and about box.</summary>
    public class SettingsForm : ModalForm
    {
        private readonly AppSettings _original;
        private readonly AppSettings _s;

        private SegmentedControl _theme, _close;
        private FlatDropdown _font, _fontSize;
        private FlatToggle _sidebar, _macrobar;
        private PaintPanel _about;

        public AppSettings Settings { get { return _original ?? _s; } }
        public AppSettings Result { get { return _s; } }

        public SettingsForm(AppSettings s)
        {
            _original = s;
            _s = s != null ? CloneSettings(s) : new AppSettings();

            TitleText = "设置";
            SubtitleText = "外观、终端字体与窗口行为";
            HeaderIconKind = "gear";

            SetDialogSize(620, 460);
            BuildUi();
            AddFooterButton("取消", false, delegate { Accepted = false; DialogResult = DialogResult.Cancel; Close(); });
            AddFooterButton("保存", true, SaveClick);
            ApplyRoundedRegion();

            LoadFromModel();
        }

        private static AppSettings CloneSettings(AppSettings s)
        {
            var c = new AppSettings();
            c.Theme = s.Theme;
            c.Accent = s.Accent;
            c.FontSize = s.FontSize;
            c.FontName = s.FontName;
            c.ShowSidebar = s.ShowSidebar;
            c.ShowMacroBar = s.ShowMacroBar;
            c.RestoreLastLayout = s.RestoreLastLayout;
            c.CloseBehavior = s.CloseBehavior;
            c.WindowX = s.WindowX;
            c.WindowY = s.WindowY;
            c.WindowW = s.WindowW;
            c.WindowH = s.WindowH;
            c.WindowState = s.WindowState;
            return c;
        }

        private void BuildUi()
        {
            const int L = 18, RW = 584;

            Caption("主题", Theme.S(L), Theme.S(14));
            HintRight("浅色主题将在后续版本提供", Theme.S(L + RW), Theme.S(14), Theme.S(220));
            _theme = new SegmentedControl();
            _theme.Options = new[] { "深色", "浅色" };
            _theme.SetOptionEnabled(1, false);
            _theme.SetBounds(Theme.S(L), Theme.S(31), Theme.S(280), Theme.S(32));
            Body.Controls.Add(_theme);

            Caption("终端字体", Theme.S(L), Theme.S(77));
            _font = new FlatDropdown();
            _font.SetItems(FontChoices());
            _font.SetBounds(Theme.S(L), Theme.S(94), Theme.S(392), Theme.S(32));
            _font.SelectedIndexChanged += delegate { UpdateAbout(); };
            Body.Controls.Add(_font);

            Caption("字号", Theme.S(420), Theme.S(77));
            _fontSize = new FlatDropdown();
            _fontSize.SetItems(new[] { "10", "11", "12", "13", "14", "15", "16", "18", "20" });
            _fontSize.SetBounds(Theme.S(420), Theme.S(94), Theme.S(120), Theme.S(32));
            _fontSize.SelectedIndexChanged += delegate { UpdateAbout(); };
            Body.Controls.Add(_fontSize);

            Caption("界面", Theme.S(L), Theme.S(142));
            AddLabel("显示会话侧栏", Theme.S(L), Theme.S(160), Theme.S(400), Theme.S(24), Theme.TextPrimary, 12.5f);
            _sidebar = new FlatToggle();
            _sidebar.SetBounds(Theme.S(540), Theme.S(163), Theme.S(34), Theme.S(18));
            Body.Controls.Add(_sidebar);

            AddLabel("显示宏工具条", Theme.S(L), Theme.S(190), Theme.S(400), Theme.S(24), Theme.TextPrimary, 12.5f);
            _macrobar = new FlatToggle();
            _macrobar.SetBounds(Theme.S(540), Theme.S(193), Theme.S(34), Theme.S(18));
            Body.Controls.Add(_macrobar);

            Caption("关闭主窗口时", Theme.S(L), Theme.S(232));
            _close = new SegmentedControl();
            _close.Options = new[] { "询问", "直接退出" };
            _close.SetBounds(Theme.S(L), Theme.S(249), Theme.S(300), Theme.S(32));
            Body.Controls.Add(_close);

            _about = new PaintPanel();
            _about.BackColor = UiKit.DialogBg;
            _about.Painter = PaintAbout;
            _about.SetBounds(Theme.S(L), Theme.S(298), Theme.S(RW), Theme.S(40));
            Body.Controls.Add(_about);
        }

        private static string[] FontChoices()
        {
            var curated = new[] { "Cascadia Mono", "Consolas", "Cascadia Code", "JetBrains Mono",
                                  "Courier New", "Lucida Console", "Microsoft YaHei UI" };
            var list = new System.Collections.Generic.List<string>();
            var installed = new System.Drawing.Text.InstalledFontCollection();
            var names = new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in installed.Families) names.Add(f.Name);
            foreach (var c in curated) if (names.Contains(c)) list.Add(c);
            if (list.Count == 0) list.Add("Consolas");
            return list.ToArray();
        }

        private void PaintAbout(Graphics g, Rectangle r)
        {
            Theme.FillRound(g, r, Theme.S(6), Theme.Blend(UiKit.DialogBg, Theme.Bg0, 0.35f));
            Theme.DrawRound(g, new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), Theme.S(6),
                Theme.BorderSoft, Math.Max(1f, Theme.SF(1f)));
            UiKit.Icon(g, "info", new Rectangle(r.X + Theme.S(12), r.Y + Theme.S(10), Theme.S(12), Theme.S(12)),
                Theme.TextTertiary);
            string l1 = "xPuTTY v0.85.1 · 基于 PuTTY 0.85 内核 · 主题 " + (_theme.SelectedIndex == 0 ? "深色" : "浅色");
            string l2 = "数据目录 " + AppContext.I.DataDir + " · 字体 " + _font.SelectedText + " " + _fontSize.SelectedText + "px";
            UiKit.Text(g, l1, UiKit.UI(10.5f), Theme.TextSecondary,
                new Rectangle(r.X + Theme.S(32), r.Y + Theme.S(4), r.Width - Theme.S(44), Theme.S(15)), UiKit.L);
            UiKit.Text(g, l2, UiKit.UI(10.5f), Theme.TextTertiary,
                new Rectangle(r.X + Theme.S(32), r.Y + Theme.S(20), r.Width - Theme.S(44), Theme.S(15)), UiKit.LE);
        }

        private void UpdateAbout()
        {
            if (_about != null) _about.Invalidate();
        }

        private void LoadFromModel()
        {
            _theme.SelectedIndex = string.Equals(_s.Theme, "light", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            if (!_font.Items.Contains(_s.FontName) && !string.IsNullOrEmpty(_s.FontName))
                _font.Items.Insert(0, _s.FontName);
            _font.Value = _s.FontName;
            if (_font.SelectedIndex < 0 && _font.Items.Count > 0) _font.SelectedIndex = 0;
            _fontSize.Value = _s.FontSize.ToString();
            if (_fontSize.SelectedIndex < 0) _fontSize.Value = "13";
            _sidebar.SetCheckedSilent(_s.ShowSidebar);
            _macrobar.SetCheckedSilent(_s.ShowMacroBar);
            _close.SelectedIndex = string.Equals(_s.CloseBehavior, "exit", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            UpdateAbout();
        }

        private void SaveClick(object sender, EventArgs e)
        {
            int size;
            _s.Theme = _theme.SelectedIndex == 1 ? "light" : "dark";
            _s.FontName = string.IsNullOrEmpty(_font.SelectedText) ? "Consolas" : _font.SelectedText;
            _s.FontSize = int.TryParse(_fontSize.SelectedText, out size) ? size : 13;
            _s.ShowSidebar = _sidebar.Checked;
            _s.ShowMacroBar = _macrobar.Checked;
            _s.CloseBehavior = _close.SelectedIndex == 1 ? "exit" : "ask";
            if (_original != null)
            {
                _original.Theme = _s.Theme;
                _original.FontName = _s.FontName;
                _original.FontSize = _s.FontSize;
                _original.ShowSidebar = _s.ShowSidebar;
                _original.ShowMacroBar = _s.ShowMacroBar;
                _original.CloseBehavior = _s.CloseBehavior;
            }
            Accepted = true;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
