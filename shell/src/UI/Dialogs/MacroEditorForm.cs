using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace XPutty
{
    /// <summary>
    /// 760x584 macro manager: reorderable list on the left, full editor on the
    /// right, footer with enabled/conflict summary.  Operates on a deep copy;
    /// after Accepted the caller can merge Result / the passed list.
    /// </summary>
    public class MacroEditorForm : ModalForm
    {
        private readonly List<MacroDef> _source;
        private List<MacroDef> _macros = new List<MacroDef>();
        private int _sel = -1;
        private bool _loading;
        private bool _recording;

        private PaintPanel _left;
        private ScrollList _list;
        private ChromeButton _addBtn;
        private FlatInput _name, _text, _hotkey;
        private ChromeButton _recordBtn;
        private SegmentedControl _scope;
        private ColorDotPicker _colors;
        private FlatToggle _enterToggle, _enabledToggle;
        private Label _hotkeyWarn;

        public List<MacroDef> Result { get { return _macros; } }

        public MacroEditorForm(List<MacroDef> macros)
        {
            _source = macros;
            if (macros != null)
                foreach (var m in macros)
                    if (m != null) _macros.Add(m.Clone());
            Reindex(_macros);
            if (_macros.Count == 0) _macros.Add(NewMacro());

            TitleText = "宏管理";
            SubtitleText = "配置工具条宏按钮，支持任意数量、拖拽排序与快捷键";
            HeaderIconKind = "keyboard";
            FooterLeftText = "已启用 0 个 · 快捷键冲突 0";

            SetDialogSize(760, 584);
            BuildLeft();
            BuildRight();
            AddFooterButton("取消", false, delegate { Accepted = false; DialogResult = DialogResult.Cancel; Close(); });
            AddFooterButton("保存", true, SaveClick);
            ApplyRoundedRegion();

            RefreshList();
            _list.SelectedIndex = 0;
            LoadMacro(0);
            UpdateFooter();
        }

        // ---------------------------------------------------------------- left
        private void BuildLeft()
        {
            _left = new PaintPanel();
            _left.BackColor = UiKit.ListBg;
            _left.Painter = PaintLeftHeader;
            _left.SetBounds(0, 0, Theme.S(244), Body.Height);
            Body.Controls.Add(_left);

            _list = new ScrollList();
            _list.BackColor = UiKit.ListBg;
            _list.RowHeight = delegate { return Theme.S(37); };
            _list.TopPad = Theme.S(0);
            _list.AllowReorder = true;
            _list.ShowScrollbar = true;
            _list.SetBounds(0, Theme.S(35), Theme.S(244), Theme.S(374));
            _list.PaintItem += PaintListItem;
            _list.SelectionChanged += delegate
            {
                if (_loading) return;
                CommitFields();
                LoadMacro(_list.SelectedIndex);
            };
            _list.Reordered += delegate(int from, int insert)
            {
                CommitFields();
                if (from < 0 || from >= _macros.Count) return;
                var m = _macros[from];
                _macros.RemoveAt(from);
                int to = insert > from ? insert - 1 : insert;
                to = Math.Max(0, Math.Min(_macros.Count, to));
                _macros.Insert(to, m);
                Reindex(_macros);
                _sel = to;
                RefreshList();
                UpdateFooter();
            };
            _list.ItemActivated += delegate { _name.FocusInput(); };
            _list.MouseUp += ListMouseUp;
            Body.Controls.Add(_list);
            // The header panel is added before the list and fills the whole left
            // column, so without this the list is hidden underneath it.
            _list.BringToFront();

            _addBtn = new ChromeButton();
            _addBtn.Style = ChromeStyle.Dashed;
            _addBtn.Icon = "plus";
            _addBtn.Text = "新增宏";
            _addBtn.Font = UiKit.UI(12f);
            _addBtn.SetBounds(Theme.S(12), Theme.S(417), Theme.S(220), Theme.S(32));
            _addBtn.Click += delegate { AddMacro(); };
            Body.Controls.Add(_addBtn);
        }

        private void ListMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            int index = _list.ItemAt(e.Location);
            if (index < 0) return;
            if (index != _sel)
            {
                CommitFields();
                _list.SelectedIndex = index;
                LoadMacro(index);
                _list.Invalidate();
            }
            var menu = DarkMenu.Create();
            menu.Items.Add(DarkMenu.Item("复制宏", delegate { DuplicateCurrent(); }));
            menu.Items.Add(DarkMenu.Item("删除宏", delegate { DeleteCurrent(); }, null, true));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(DarkMenu.Item("上移", delegate { MoveCurrent(-1); }));
            menu.Items.Add(DarkMenu.Item("下移", delegate { MoveCurrent(1); }));
            menu.Show(_list, e.Location);
        }

        private void MoveCurrent(int delta)
        {
            if (_sel < 0 || _sel >= _macros.Count) return;
            int to = _sel + delta;
            if (to < 0 || to >= _macros.Count) return;
            CommitFields();
            var m = _macros[_sel];
            _macros.RemoveAt(_sel);
            _macros.Insert(to, m);
            Reindex(_macros);
            _sel = to;
            RefreshList();
            _list.SelectedIndex = _sel;
            LoadMacro(_sel);
            UpdateFooter();
        }

        private void PaintLeftHeader(Graphics g, Rectangle r)
        {
            int h = Theme.S(35);
            UiKit.Text(g, "宏按钮", UiKit.UI(11.5f), Theme.TextSecondary,
                new Rectangle(Theme.S(17), 0, r.Width - Theme.S(34), h), UiKit.L);
            int enabled = 0;
            foreach (var m in _macros) if (m.Enabled) enabled++;
            string cnt = enabled + " / " + _macros.Count;
            UiKit.Text(g, cnt, UiKit.Mono(10f), Theme.TextTertiary,
                new Rectangle(Theme.S(17), 0, r.Width - Theme.S(34), h), UiKit.R);
            UiKit.LineV(g, r.Width - 1, 0, r.Height, Theme.Border);
        }

        private void PaintListItem(object sender, ItemPaintEventArgs e)
        {
            var m = e.Item as MacroDef;
            if (m == null) return;
            Graphics g = e.G;
            var r = new Rectangle(Theme.S(10), e.Bounds.Y + Theme.S(1),
                                  e.Bounds.Width - Theme.S(20), e.Bounds.Height - Theme.S(2));
            if (e.Selected)
            {
                Theme.FillRound(g, r, Theme.S(6), UiKit.ItemSelBg);
                Theme.DrawRound(g, new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), Theme.S(6),
                    UiKit.ItemSelBorder, Math.Max(1f, Theme.SF(1f)));
            }
            else if (e.Hover)
            {
                Theme.FillRound(g, r, Theme.S(6), Theme.HoverRow);
            }

            int cy = e.Bounds.Y + e.Bounds.Height / 2;
            UiKit.Icon(g, "grip",
                new Rectangle(r.X + Theme.S(10), cy - Theme.S(7), Theme.S(10), Theme.S(14)),
                Theme.TextTertiary);
            UiKit.Dot(g, new Point(r.X + Theme.S(30), cy), Theme.S(6), UiKit.TagColor(m.Color));

            int capW = 0;
            if (!string.IsNullOrEmpty(m.Hotkey))
            {
                Font capFont = UiKit.Mono(9.5f);
                var cap = UiKit.KeyCapRect(g, r.Right - Theme.S(7), cy, m.Hotkey, capFont);
                UiKit.KeyCap(g, cap, m.Hotkey, capFont,
                    e.Selected ? Theme.TextSecondary : Theme.TextTertiary, UiKit.KeyCapBorder);
                capW = cap.Width + Theme.S(10);
            }

            Color nameColor = !m.Enabled ? Theme.TextTertiary
                            : e.Selected ? Theme.TextPrimary : Theme.TextSecondary;
            var tr = new Rectangle(r.X + Theme.S(43), e.Bounds.Y,
                                   Math.Max(0, r.Width - Theme.S(43) - capW - Theme.S(6)), e.Bounds.Height);
            UiKit.Text(g, m.Name, UiKit.UI(12f), nameColor, tr, UiKit.LE);
        }

        // --------------------------------------------------------------- right
        private void BuildRight()
        {
            int L = 262, C1W = 225, C2 = 563, RW = 478;

            Caption("名称", Theme.S(L), Theme.S(16));
            AddLabel("*", Theme.S(L) + UiKit.Measure("名称", UiKit.UI(11.5f)) + Theme.S(3),
                Theme.S(16), Theme.S(12), Theme.S(15), Theme.Danger, 11.5f);
            HintRight("显示在工具条按钮上，建议 2~6 个字", Theme.S(L + RW), Theme.S(16), Theme.S(240));

            _name = new FlatInput();
            _name.SetBounds(Theme.S(L), Theme.S(35), Theme.S(RW), Theme.S(32));
            _name.MaxLength = 16;
            _name.TextChanged += delegate { if (!_loading) MarkDirty(); };
            Body.Controls.Add(_name);

            Caption("发送内容", Theme.S(L), Theme.S(84));
            HintRight("\\r\\n = 回车换行，支持 $HOST / $USER 变量", Theme.S(L + RW), Theme.S(84), Theme.S(300));

            _text = new FlatInput(true);
            _text.SetBounds(Theme.S(L), Theme.S(103), Theme.S(RW), Theme.S(76));
            _text.Mono = true;
            _text.TextChanged += delegate { if (!_loading) MarkDirty(); };
            Body.Controls.Add(_text);

            Caption("快捷键", Theme.S(L), Theme.S(197));
            _hotkeyWarn = AddLabel("", Theme.S(L + 60), Theme.S(197),
                Theme.S(C2 - (L + 60) - Theme.S(10)), Theme.S(15), Theme.Danger, 10.5f, false,
                ContentAlignment.MiddleRight);

            _hotkey = new FlatInput();
            _hotkey.Mono = true;
            _hotkey.SetBounds(Theme.S(L), Theme.S(216), Theme.S(C1W), Theme.S(32));
            _hotkey.TextChanged += delegate
            {
                if (_loading) return;
                MarkDirty();
                UpdateConflict();
            };
            Body.Controls.Add(_hotkey);

            _recordBtn = new ChromeButton();
            _recordBtn.Style = ChromeStyle.Flat;
            _recordBtn.Icon = "record";
            _recordBtn.IconTint = Theme.Danger;
            _recordBtn.Text = "录制";
            _recordBtn.PadX = 8;
            _recordBtn.Font = UiKit.UI(11.5f);
            _recordBtn.SetBounds(Theme.S(L + C1W + 8), Theme.S(216), Theme.S(65), Theme.S(32));
            _recordBtn.Click += delegate { ToggleRecording(); };
            Body.Controls.Add(_recordBtn);

            Caption("作用范围", Theme.S(C2), Theme.S(197));
            _scope = new SegmentedControl();
            _scope.Options = new[] { "全局", "当前会话" };
            _scope.SetBounds(Theme.S(C2), Theme.S(216), Theme.S(177), Theme.S(32));
            Body.Controls.Add(_scope);

            Caption("颜色标签", Theme.S(L), Theme.S(259));
            _colors = new ColorDotPicker();
            _colors.SetBounds(Theme.S(L), Theme.S(281), Theme.S(160), Theme.S(24));
            _colors.Changed += delegate { if (!_loading) { MarkDirty(); _list.Invalidate(); } };
            Body.Controls.Add(_colors);

            Caption("自动回车", Theme.S(C2), Theme.S(259));
            _enterToggle = new FlatToggle();
            _enterToggle.SetBounds(Theme.S(C2), Theme.S(282), Theme.S(34), Theme.S(18));
            _enterToggle.CheckedChanged += delegate { if (!_loading) MarkDirty(); };
            Body.Controls.Add(_enterToggle);
            AddLabel("追加 \\r", Theme.S(C2 + 44), Theme.S(282), Theme.S(90), Theme.S(18), Theme.TextSecondary, 11.5f);

            Caption("启用", Theme.S(L), Theme.S(324));
            _enabledToggle = new FlatToggle();
            _enabledToggle.SetBounds(Theme.S(L), Theme.S(348), Theme.S(34), Theme.S(18));
            _enabledToggle.CheckedChanged += delegate
            {
                if (_loading) return;
                MarkDirty();
                _list.Invalidate();
                UpdateFooter();
            };
            Body.Controls.Add(_enabledToggle);
            AddLabel("在宏工具栏中显示", Theme.S(L + 44), Theme.S(348), Theme.S(220), Theme.S(18), Theme.TextSecondary, 11.5f);
            // The "disabled" hint is painted on the body itself (two explicit
            // lines) so it can never be clipped by a Label's text box or run
            // into the tip panel below.
            Body.Painter = PaintBodyHint;

            var tip = new PaintPanel();
            tip.BackColor = UiKit.DialogBg;
            tip.Painter = PaintTip;
            tip.SetBounds(Theme.S(L), Theme.S(383), Theme.S(RW), Theme.S(65));
            Body.Controls.Add(tip);
        }

        private const int C2Col = 563;

        private void PaintBodyHint(Graphics g, Rectangle r)
        {
            Font f = UiKit.UI(10.5f);
            UiKit.Text(g, "关闭后隐藏于工具条", f, Theme.TextTertiary,
                new Rectangle(Theme.S(C2Col), Theme.S(318), Theme.S(178), Theme.S(15)), UiKit.L);
            UiKit.Text(g, "且不响应快捷键", f, Theme.TextTertiary,
                new Rectangle(Theme.S(C2Col), Theme.S(333), Theme.S(178), Theme.S(15)), UiKit.L);
        }

        private void PaintTip(Graphics g, Rectangle r)
        {
            Theme.FillRound(g, r, Theme.S(6), Theme.Blend(UiKit.DialogBg, Theme.Bg0, 0.35f));
            Theme.DrawRound(g, new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), Theme.S(6),
                Theme.BorderSoft, Math.Max(1f, Theme.SF(1f)));
            UiKit.Icon(g, "info", new Rectangle(r.X + Theme.S(12), r.Y + Theme.S(12), Theme.S(12), Theme.S(12)),
                Theme.TextTertiary);
            UiKit.Text(g,
                "提示：宏内容发送到当前焦点窗格；SSH 会话默认追加 \\n，串口会话追加 \\r。拖拽左侧手柄可调整按钮顺序。",
                UiKit.UI(10.5f), Theme.TextSecondary,
                new Rectangle(r.X + Theme.S(32), r.Y + Theme.S(8), r.Width - Theme.S(44), r.Height - Theme.S(16)),
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix
                | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        }

        // -------------------------------------------------------------- state
        private void MarkDirty()
        {
            if (_list != null) _list.Invalidate();
        }

        private MacroDef NewMacro()
        {
            var m = new MacroDef();
            m.Name = "新宏";
            m.Color = UiKit.TagName(_macros.Count);
            m.Sort = _macros.Count;
            m.Enabled = true;
            m.SendEnter = true;
            m.Scope = "global";
            return m;
        }

        private void AddMacro()
        {
            CommitFields();
            var m = NewMacro();
            m.Name = "新宏 " + (_macros.Count + 1);
            _macros.Add(m);
            Reindex(_macros);
            _sel = _macros.Count - 1;
            RefreshList();
            _list.SelectedIndex = _sel;
            LoadMacro(_sel);
            _name.FocusInput();
            _name.SelectAllText();
            UpdateFooter();
        }

        private void DeleteCurrent()
        {
            if (_sel < 0 || _sel >= _macros.Count) return;
            _macros.RemoveAt(_sel);
            if (_macros.Count == 0)
            {
                _sel = -1;
                RefreshList();
                LoadMacro(-1);
                UpdateFooter();
                return;
            }
            Reindex(_macros);
            _sel = Math.Min(_sel, _macros.Count - 1);
            RefreshList();
            _list.SelectedIndex = _sel;
            LoadMacro(_sel);
            UpdateFooter();
        }

        private void DuplicateCurrent()
        {
            if (_sel < 0 || _sel >= _macros.Count) return;
            CommitFields();
            var c = _macros[_sel].Clone();
            c.Id = Guid.NewGuid().ToString("N");
            c.Name = c.Name + " 副本";
            c.Hotkey = "";
            _macros.Insert(_sel + 1, c);
            Reindex(_macros);
            _sel++;
            RefreshList();
            _list.SelectedIndex = _sel;
            LoadMacro(_sel);
            UpdateFooter();
        }

        private static void Reindex(List<MacroDef> list)
        {
            list.Sort(delegate(MacroDef a, MacroDef b)
            {
                if (a == null) return b == null ? 0 : -1;
                if (b == null) return 1;
                return a.Sort.CompareTo(b.Sort);
            });
            for (int i = 0; i < list.Count; i++) list[i].Sort = i;
        }

        private void RefreshList()
        {
            _loading = true;
            var arr = new object[_macros.Count];
            for (int i = 0; i < _macros.Count; i++) arr[i] = _macros[i];
            _list.SetItems(arr);
            _list.SelectedIndex = _sel;
            _list.InvalidateList();
            _loading = false;
            PostRedraw(_left);
        }

        private void PostRedraw(Control c)
        {
            if (c != null) c.Invalidate();
        }

        private void LoadMacro(int index)
        {
            _sel = index;
            _loading = true;
            if (index < 0 || index >= _macros.Count)
            {
                _name.Text = "";
                _text.Text = "";
                _hotkey.Text = "";
                _colors.SelectedColor = "";
                _enterToggle.SetCheckedSilent(false);
                _enabledToggle.SetCheckedSilent(false);
                _scope.SelectedIndex = 0;
                _loading = false;
                SetEditorEnabled(false);
                return;
            }
            SetEditorEnabled(true);
            var m = _macros[index];
            _name.Text = m.Name;
            _text.Text = m.Text;
            _hotkey.Text = PrettyHotkey(m.Hotkey);
            _colors.SelectedColor = m.Color;
            _enterToggle.SetCheckedSilent(m.SendEnter);
            _enabledToggle.SetCheckedSilent(m.Enabled);
            _scope.SelectedIndex = (m.Scope ?? "global").StartsWith("session") ? 1 : 0;
            _hotkeyWarn.Text = "";
            _loading = false;
            UpdateConflict();
            PostRedraw(_left);
        }

        private void SetEditorEnabled(bool on)
        {
            _name.Enabled = on;
            _text.Enabled = on;
            _hotkey.Enabled = on;
            _recordBtn.Enabled = on;
            _scope.Enabled = on;
            _colors.Enabled = on;
            _enterToggle.Enabled = on;
            _enabledToggle.Enabled = on;
        }

        private void CommitFields()
        {
            if (_loading) return;
            if (_sel < 0 || _sel >= _macros.Count) return;
            var m = _macros[_sel];
            m.Name = SafeTrim(_name.Text, 16);
            if (string.IsNullOrEmpty(m.Name)) m.Name = "未命名宏";
            m.Text = _text.Text ?? "";
            m.Hotkey = NormalizeHotkey(_hotkey.Text);
            m.Color = string.IsNullOrEmpty(_colors.SelectedColor) ? "blue" : _colors.SelectedColor;
            m.SendEnter = _enterToggle.Checked;
            m.Enabled = _enabledToggle.Checked;
            m.Scope = _scope.SelectedIndex == 1 ? "session:" : "global";
            m.Sort = _sel;
        }

        private static string SafeTrim(string s, int max)
        {
            s = (s ?? "").Trim();
            if (s.Length > max) s = s.Substring(0, max);
            return s;
        }

        private static string NormalizeHotkey(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Trim().Replace(" + ", "+").Replace(" ", "");
            return s;
        }

        private static string PrettyHotkey(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace(" + ", "+").Replace("+", " + ");
        }

        private void ToggleRecording()
        {
            if (_recording)
            {
                _recording = false;
                _recordBtn.Style = ChromeStyle.Flat;
                _recordBtn.Text = "录制";
                _hotkey.Text = _originalHotkey;
                _hotkeyWarn.Text = "";
                return;
            }
            CommitFields();
            _originalHotkey = _hotkey.Text;
            _recording = true;
            _recordBtn.Style = ChromeStyle.Primary;
            _recordBtn.Text = "录制";
            _hotkey.Text = "";
            _hotkeyWarn.ForeColor = Theme.TextSecondary;
            _hotkeyWarn.Text = "按下组合键，Esc 取消";
            _recordBtn.Focus();
        }

        private string _originalHotkey = "";

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (_recording)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                if (e.KeyCode == Keys.Escape)
                {
                    _recording = false;
                    _recordBtn.Style = ChromeStyle.Flat;
                    _hotkey.Text = _originalHotkey;
                    UpdateConflict();
                    return;
                }
                if (e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.ShiftKey
                    || e.KeyCode == Keys.Menu || e.KeyCode == Keys.LWin || e.KeyCode == Keys.RWin)
                    return;
                string combo = BuildHotkey(e);
                _recording = false;
                _recordBtn.Style = ChromeStyle.Flat;
                _hotkey.Text = PrettyHotkey(combo);
                UpdateConflict();
                return;
            }

            if (e.KeyCode == Keys.Delete && _list.Focused)
            {
                DeleteCurrent();
                e.Handled = true;
                return;
            }
            base.OnKeyDown(e);
        }

        private static string BuildHotkey(KeyEventArgs e)
        {
            var parts = new List<string>();
            if (e.Control) parts.Add("Ctrl");
            if (e.Shift) parts.Add("Shift");
            if (e.Alt) parts.Add("Alt");
            string key = e.KeyCode.ToString();
            if (key.StartsWith("D") && key.Length == 2 && char.IsDigit(key[1]))
                key = key.Substring(1);
            else if (e.KeyCode >= Keys.NumPad0 && e.KeyCode <= Keys.NumPad9)
                key = ((int)e.KeyCode - (int)Keys.NumPad0).ToString();
            else if (e.KeyCode == Keys.Oemtilde) key = "`";
            else if (e.KeyCode == Keys.OemMinus) key = "-";
            else if (e.KeyCode == Keys.Oemplus) key = "=";
            parts.Add(key);
            return string.Join("+", parts.ToArray());
        }

        private void UpdateConflict()
        {
            if (_loading || _sel < 0 || _sel >= _macros.Count) return;
            string hk = NormalizeHotkey(_hotkey.Text);
            _hotkeyWarn.ForeColor = Theme.Danger;
            if (string.IsNullOrEmpty(hk)) { _hotkeyWarn.Text = ""; return; }
            for (int i = 0; i < _macros.Count; i++)
            {
                if (i == _sel) continue;
                if (string.Equals(NormalizeHotkey(_macros[i].Hotkey), hk, StringComparison.OrdinalIgnoreCase))
                {
                    _hotkeyWarn.Text = "与「" + _macros[i].Name + "」冲突";
                    return;
                }
            }
            _hotkeyWarn.Text = "";
        }

        private void UpdateFooter()
        {
            int enabled = 0, conflicts = 0;
            foreach (var m in _macros) if (m.Enabled) enabled++;
            for (int i = 0; i < _macros.Count; i++)
            {
                string hk = NormalizeHotkey(_macros[i].Hotkey);
                if (string.IsNullOrEmpty(hk)) continue;
                for (int j = i + 1; j < _macros.Count; j++)
                    if (string.Equals(NormalizeHotkey(_macros[j].Hotkey), hk, StringComparison.OrdinalIgnoreCase))
                        conflicts++;
            }
            FooterLeftText = "已启用 " + enabled + " 个 · 快捷键冲突 " + conflicts;
            FooterLeftColor = conflicts > 0 ? Theme.Warning : Theme.TextSecondary;
            if (Footer != null) Footer.Invalidate();
            if (_left != null) _left.Invalidate();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_name != null)
            {
                _name.FocusInput();
                _name.CaretToEnd();
            }
        }

        private void SaveClick(object sender, EventArgs e)
        {
            CommitFields();
            // last-chance validation: a macro with no name gets a fallback name
            for (int i = 0; i < _macros.Count; i++)
            {
                if (_macros[i] == null) continue;
                if (string.IsNullOrEmpty(_macros[i].Name)) _macros[i].Name = "宏 " + (i + 1);
                _macros[i].Sort = i;
            }
            Reindex(_macros);
            if (_source != null)
            {
                _source.Clear();
                foreach (var m in _macros) _source.Add(m.Clone());
            }
            Accepted = true;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
