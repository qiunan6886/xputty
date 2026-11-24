using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace XPutty
{
    /// <summary>
    /// 240px session sidebar: new-session button, live search, grouped session
    /// tree (27px group rows / 45px session rows with protocol badge, host and
    /// status dot), bottom shortcuts, right-click menu and smooth scrolling.
    /// </summary>
    public class SessionSidebar : Control
    {
        private enum RowKind { Group, Session }

        private sealed class Row
        {
            public RowKind Kind;
            public string Group = "";
            public SessionDef Session;
            public int Y;
            public int H;
        }

        // ---- model -----------------------------------------------------
        private List<SessionDef> _all = new List<SessionDef>();
        private readonly List<Row> _rows = new List<Row>();
        private readonly HashSet<string> _collapsed = new HashSet<string>();
        private readonly Dictionary<string, int> _state = new Dictionary<string, int>();
        private string _selectedId;
        private int _hoverRow = -1;
        private int _scroll;

        // ---- bottom links ----------------------------------------------
        private readonly List<KeyValuePair<Rectangle, Action>> _links =
            new List<KeyValuePair<Rectangle, Action>>();
        private int _hoverLink = -1;

        // ---- chrome ----------------------------------------------------
        private readonly ChromeButton _newBtn;
        private readonly FlatInput _search;
        private const int SearchY = 56;
        private const int SectionY = 96;
        private const int TreeY = 123;
        private const int GroupH = 27;
        private const int SessionH = 45;

        public event Action<SessionDef> SessionActivated;
        public event Action NewSessionRequested;
        public event Action ImportPuttyRequested;
        public event Action<SessionDef> EditSessionRequested;
        public event Action<SessionDef> DeleteSessionRequested;
        public event Action SerialScanRequested;
        public event Action KeyManagerRequested;
        public event Action<SessionDef> DuplicateSessionRequested;

        public SessionSidebar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                     | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw
                     | ControlStyles.Selectable, true);
            BackColor = Theme.SidebarBg;
            TabStop = true;
            Font = UiKit.UI(12.5f);
            Size = new Size(Theme.S(240), Theme.S(600));

            _newBtn = new ChromeButton();
            _newBtn.Style = ChromeStyle.Primary;
            _newBtn.Icon = "plus";
            _newBtn.Text = "新建会话";
            _newBtn.RightHint = "Ctrl N";
            _newBtn.Font = UiKit.UI(12.5f);
            _newBtn.Click += delegate { Raise(NewSessionRequested); };
            Controls.Add(_newBtn);

            _search = new FlatInput();
            _search.LeftIcon = "search";
            _search.RightHint = "Ctrl K";
            _search.Placeholder = "搜索会话或主机";
            _search.TextChanged += delegate
            {
                _scroll = 0;
                Rebuild();
                Invalidate();
            };
            Controls.Add(_search);

            Relayout();
        }

        private static void Raise(Action a)
        {
            if (a != null) a();
        }

        // ---- public API (SHELL_SPEC §8) --------------------------------
        public void SetSessions(List<SessionDef> sessions, string filter)
        {
            _all = sessions ?? new List<SessionDef>();
            if (!string.IsNullOrEmpty(filter) && _search.Text != filter)
                _search.Text = filter;   // TextChanged rebuilds
            else
                Rebuild();
            EnsureValidSelection();
            Invalidate();
        }

        private void EnsureValidSelection()
        {
            bool valid = false;
            if (!string.IsNullOrEmpty(_selectedId))
            {
                foreach (var s in _all)
                    if (s != null && s.Id == _selectedId) { valid = true; break; }
            }
            if (valid) return;
            foreach (var s in _all)
            {
                if (s == null) continue;
                _selectedId = s.Id;
                return;
            }
            _selectedId = null;
        }

        public void InvalidateList()
        {
            Rebuild();
            Invalidate();
        }

        public SessionDef SelectedSession
        {
            get
            {
                if (string.IsNullOrEmpty(_selectedId)) return null;
                foreach (var s in _all) if (s != null && s.Id == _selectedId) return s;
                return null;
            }
        }

        public void SelectSession(string id)
        {
            _selectedId = id;
            Invalidate();
        }

        public void FocusSearch()
        {
            Focus();
            _search.FocusInput();
        }

        public void ClearFilter()
        {
            _search.Text = "";
        }

        /// <summary>Optional live state hook: 0 = offline, 1 = online/idle, 2 = busy.</summary>
        public void SetSessionState(string sessionId, int state)
        {
            if (string.IsNullOrEmpty(sessionId)) return;
            _state[sessionId] = state;
            Invalidate();
        }

        // ---- layout ----------------------------------------------------
        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Relayout();
        }

        private void Relayout()
        {
            if (_newBtn == null || _search == null) return;
            int w = ClientSize.Width;
            int btnW = Math.Max(Theme.S(80), w - Theme.S(24));
            _newBtn.SetBounds(Theme.S(12), Theme.S(12), btnW, Theme.S(34));
            _search.SetBounds(Theme.S(12), Theme.S(SearchY), btnW, Theme.S(32));
            LayoutLinks();
            Rebuild();
        }

        private int LinksTop()
        {
            return ClientSize.Height - Theme.S(8) - Theme.S(81);
        }

        private int CardTop()
        {
            return LinksTop() - Theme.S(6) - Theme.S(64);
        }

        private int TreeBottom
        {
            get { return Math.Max(Theme.S(TreeY + GroupH), CardTop() - Theme.S(8)); }
        }

        private Rectangle TreeViewport
        {
            get
            {
                return new Rectangle(0, Theme.S(TreeY), ClientSize.Width,
                    Math.Max(0, TreeBottom - Theme.S(TreeY)));
            }
        }

        private void LayoutLinks()
        {
            _links.Clear();
            int y = LinksTop();
            int w = ClientSize.Width - Theme.S(16);
            _links.Add(new KeyValuePair<Rectangle, Action>(
                new Rectangle(Theme.S(8), y, w, Theme.S(27)),
                delegate { Raise(SerialScanRequested); }));
            _links.Add(new KeyValuePair<Rectangle, Action>(
                new Rectangle(Theme.S(8), y + Theme.S(27), w, Theme.S(27)),
                delegate { Raise(KeyManagerRequested); }));
            _links.Add(new KeyValuePair<Rectangle, Action>(
                new Rectangle(Theme.S(8), y + Theme.S(54), w, Theme.S(27)),
                delegate { Raise(ImportPuttyRequested); }));
        }

        // ---- row model -------------------------------------------------
        private string FilterText { get { return (_search == null ? "" : (_search.Text ?? "")).Trim(); } }

        private static bool Matches(SessionDef s, string filter)
        {
            if (string.IsNullOrEmpty(filter)) return true;
            string f = filter.ToLowerInvariant();
            return Contains(s.Name, f) || Contains(s.Host, f) || Contains(s.SerialPort, f)
                || Contains(s.Group, f) || Contains(s.User, f);
        }

        private static bool Contains(string v, string f)
        {
            return !string.IsNullOrEmpty(v) && v.ToLowerInvariant().IndexOf(f, StringComparison.Ordinal) >= 0;
        }

        private void Rebuild()
        {
            _rows.Clear();
            if (_all == null) _all = new List<SessionDef>();
            string filter = FilterText;
            bool filtering = !string.IsNullOrEmpty(filter);

            var groups = new List<string>();
            var byGroup = new Dictionary<string, List<SessionDef>>();
            foreach (var s in _all)
            {
                if (s == null) continue;
                if (!Matches(s, filter)) continue;
                string g = string.IsNullOrEmpty(s.Group) ? "未分组" : s.Group;
                if (!byGroup.ContainsKey(g)) { byGroup[g] = new List<SessionDef>(); groups.Add(g); }
                byGroup[g].Add(s);
            }

            int y = Theme.S(TreeY) - _scroll;
            foreach (var g in groups)
            {
                var list = byGroup[g];
                bool collapsed = !filtering && _collapsed.Contains(g);
                _rows.Add(new Row { Kind = RowKind.Group, Group = g, Y = y, H = Theme.S(GroupH) });
                y += Theme.S(GroupH);
                if (collapsed) continue;
                foreach (var s in list)
                {
                    _rows.Add(new Row { Kind = RowKind.Session, Session = s, Group = g, Y = y, H = Theme.S(SessionH) });
                    y += Theme.S(SessionH);
                }
            }
            ClampScroll();
        }

        private int ContentBottom()
        {
            int y = Theme.S(TreeY);
            foreach (var r in _rows) y = Math.Max(y, r.Y + r.H + _scroll);
            return y;
        }

        private int MaxScroll()
        {
            return Math.Max(0, ContentBottom() - TreeBottom);
        }

        private void ClampScroll()
        {
            _scroll = Math.Max(0, Math.Min(MaxScroll(), _scroll));
        }

        private int CountSessionRows()
        {
            int n = 0;
            foreach (var r in _rows) if (r.Kind == RowKind.Session) n++;
            return n;
        }

        // ---- painting --------------------------------------------------
        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Theme.SidebarBg);
            ClampScroll();

            // section header
            UiKit.Text(g, "会话", UiKit.UI(11.5f), Theme.TextSecondary,
                new Rectangle(Theme.S(12), Theme.S(SectionY), Theme.S(120), Theme.S(24)), UiKit.L);
            if (_all != null)
            {
                int total = 0;
                foreach (var s in _all) if (s != null) total++;
                string cnt = CountSessionRows() + " / " + total;
                UiKit.Text(g, cnt, UiKit.Mono(10f), Theme.TextTertiary,
                    new Rectangle(Theme.S(100), Theme.S(SectionY), ClientSize.Width - Theme.S(112), Theme.S(24)),
                    UiKit.R);
            }

            // tree
            var oldClip = g.Clip;
            g.SetClip(TreeViewport);
            if (_rows.Count == 0)
            {
                string empty = string.IsNullOrEmpty(FilterText) ? "还没有会话，点击上方新建" : "没有匹配的会话";
                UiKit.Text(g, empty, UiKit.UI(11.5f), Theme.TextTertiary,
                    new Rectangle(Theme.S(12), Theme.S(TreeY) + Theme.S(16),
                        ClientSize.Width - Theme.S(24), Theme.S(40)), UiKit.L);
            }
            foreach (var r in _rows)
            {
                if (r.Y + r.H < TreeViewport.Y || r.Y > TreeViewport.Bottom) continue;
                if (r.Kind == RowKind.Group) PaintGroupRow(g, r);
                else PaintSessionRow(g, r);
            }
            g.Clip = oldClip;

            PaintScrollbar(g);
            UiKit.LineV(g, ClientSize.Width - 1, 0, ClientSize.Height, Theme.Border);
            PaintBottom(g);
        }

        private bool IsHover(Row r)
        {
            return _hoverRow >= 0 && _hoverRow < _rows.Count && _rows[_hoverRow] == r;
        }

        private void PaintGroupRow(Graphics g, Row r)
        {
            int cy = r.Y + r.H / 2;
            if (IsHover(r))
                Theme.FillRound(g, new Rectangle(Theme.S(8), r.Y + Theme.S(2),
                    ClientSize.Width - Theme.S(16), r.H - Theme.S(4)), Theme.S(6), UiKit.HoverRow);
            if (r.Y < TreeViewport.Y || r.Y + r.H > TreeViewport.Bottom) return;
            bool collapsed = _collapsed.Contains(r.Group) && string.IsNullOrEmpty(FilterText);
            UiKit.Chevron(g, new Rectangle(Theme.S(14), cy - Theme.S(6), Theme.S(12), Theme.S(12)),
                collapsed ? 1 : 0, Theme.TextTertiary);
            UiKit.Icon(g, collapsed ? "folder" : "folder-open",
                new Rectangle(Theme.S(31), cy - Theme.S(7), Theme.S(14), Theme.S(14)), Theme.TextTertiary);
            UiKit.Text(g, r.Group, UiKit.UI(12f), Theme.TextSecondary,
                new Rectangle(Theme.S(51), r.Y, ClientSize.Width - Theme.S(51) - Theme.S(34), r.H), UiKit.LE);
            int count = 0;
            foreach (var s in _all)
            {
                if (s == null) continue;
                if ((string.IsNullOrEmpty(s.Group) ? "未分组" : s.Group) != r.Group) continue;
                if (Matches(s, FilterText)) count++;
            }
            UiKit.Text(g, count.ToString(), UiKit.Mono(10f), Theme.TextTertiary,
                new Rectangle(ClientSize.Width - Theme.S(30), r.Y, Theme.S(18), r.H), UiKit.R);
        }

        private void PaintSessionRow(Graphics g, Row r)
        {
            SessionDef s = r.Session;
            bool selected = s.Id == _selectedId;
            bool hover = IsHover(r);
            var rr = new Rectangle(Theme.S(8), r.Y, ClientSize.Width - Theme.S(16), r.H);

            if (selected)
            {
                Theme.FillRound(g, rr, Theme.S(6), UiKit.SelectRow);
                Theme.DrawRound(g, new Rectangle(rr.X, rr.Y, rr.Width - 1, rr.Height - 1), Theme.S(6),
                    UiKit.SelectBorder, Math.Max(1f, Theme.SF(1f)));
                Theme.FillRound(g, new Rectangle(rr.X + Theme.S(1), r.Y + (r.H - Theme.S(27)) / 2,
                    Theme.S(2), Theme.S(27)), Theme.S(1), Theme.Accent);
            }
            else if (hover)
            {
                Theme.FillRound(g, rr, Theme.S(6), UiKit.HoverRow);
            }

            // Rows that straddle the viewport edge are painted background-only:
            // TextRenderer ignores Graphics clip regions, so text would bleed
            // into the footer area.
            if (r.Y < TreeViewport.Y || r.Y + r.H > TreeViewport.Bottom) return;

            // protocol badge 34x18
            Color pc = Theme.ProtocolColor(s.Protocol);
            var br = new Rectangle(rr.X + Theme.S(8), r.Y + (r.H - Theme.S(18)) / 2, Theme.S(34), Theme.S(18));
            Theme.FillRound(g, br, Theme.S(4), Color.FromArgb(38, pc));
            Theme.DrawRound(g, br, Theme.S(4), Color.FromArgb(64, pc), Math.Max(1f, Theme.SF(1f)));
            UiKit.Text(g, ProtocolText(s.Protocol), UiKit.MonoBold(7.5f),
                Theme.Blend(pc, Color.White, 0.3f), br, UiKit.LC);

            int tx = br.Right + Theme.S(8);
            int right = ClientSize.Width - Theme.S(28);
            UiKit.Text(g, s.Name, selected ? UiKit.UIBold(12.5f) : UiKit.UI(12.5f), Theme.TextPrimary,
                new Rectangle(tx, r.Y + Theme.S(7), Math.Max(Theme.S(20), right - tx), Theme.S(17)), UiKit.LE);
            UiKit.Text(g, s.Subtitle, UiKit.Mono(10f), Theme.TextSecondary,
                new Rectangle(tx, r.Y + Theme.S(23), Math.Max(Theme.S(20), right - tx), Theme.S(15)), UiKit.LE);

            int state = 1;
            if (_state.ContainsKey(s.Id)) state = _state[s.Id];
            else if (s.Protocol == ProtocolKind.Serial) state = 2;
            Color dot = state == 0 ? Theme.TextDisabled : state == 2 ? Theme.Warning : Theme.Success;
            var center = new Point(ClientSize.Width - Theme.S(20), r.Y + r.H / 2);
            if (state == 1) UiKit.DotHalo(g, center, dot);
            else UiKit.Dot(g, center, Theme.S(6), dot);
        }

        private static string ProtocolText(ProtocolKind p)
        {
            switch (p)
            {
                case ProtocolKind.Ssh: return "SSH";
                case ProtocolKind.Telnet: return "TELNET";
                case ProtocolKind.Serial: return "SERIAL";
                default: return "RAW";
            }
        }

        private Rectangle ThumbRect()
        {
            int max = MaxScroll();
            if (max <= 0) return Rectangle.Empty;
            int trackTop = Theme.S(TreeY);
            int trackH = TreeBottom - Theme.S(TreeY);
            int thumbH = Math.Max(Theme.S(28), trackH * trackH / Math.Max(1, trackH + max));
            int pos = (int)((long)(trackH - thumbH) * _scroll / max);
            return new Rectangle(ClientSize.Width - Theme.S(8), trackTop + pos, Theme.S(6), thumbH);
        }

        private void PaintScrollbar(Graphics g)
        {
            var t = ThumbRect();
            if (t.IsEmpty) return;
            Theme.FillRound(g, t, Theme.S(3), Theme.Border);
        }

        private void PaintBottom(Graphics g)
        {
            int cardTop = CardTop();
            var card = new Rectangle(Theme.S(12), cardTop, ClientSize.Width - Theme.S(24), Theme.S(64));
            Theme.FillRound(g, card, Theme.S(8), UiKit.CardBg);
            Theme.DrawRound(g, new Rectangle(card.X, card.Y, card.Width - 1, card.Height - 1), Theme.S(8),
                UiKit.CardBorder, Math.Max(1f, Theme.SF(1f)));
            UiKit.Icon(g, "bulb", new Rectangle(card.X + Theme.S(14), card.Y + Theme.S(13),
                Theme.S(14), Theme.S(14)), Theme.Accent);
            UiKit.Text(g, "分屏提示", UiKit.UIBold(11.5f), Theme.TextPrimary,
                new Rectangle(card.X + Theme.S(34), card.Y + Theme.S(9),
                    card.Width - Theme.S(44), Theme.S(16)), UiKit.L);
            UiKit.Text(g, "拖拽标签到窗格边缘，可拆分为 2×2、1×3 等 12 种布局", UiKit.UI(10f), Theme.TextTertiary,
                new Rectangle(card.X + Theme.S(14), card.Y + Theme.S(28),
                    card.Width - Theme.S(26), Theme.S(32)),
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPrefix
                | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);

            UiKit.LineH(g, Theme.S(12), ClientSize.Width - Theme.S(12), LinksTop() - Theme.S(6), Theme.BorderSoft);

            for (int i = 0; i < _links.Count; i++)
            {
                var r = _links[i].Key;
                if (i == _hoverLink) Theme.FillRound(g, r, Theme.S(6), UiKit.HoverRow);
                string icon = i == 0 ? "plug" : i == 1 ? "key" : "download";
                string text = i == 0 ? "串口扫描" : i == 1 ? "密钥与凭据" : "从 PuTTY 导入会话";
                UiKit.Icon(g, icon, new Rectangle(r.X + Theme.S(8), r.Y + (Theme.S(27) - Theme.S(14)) / 2,
                    Theme.S(14), Theme.S(14)), Theme.TextTertiary);
                UiKit.Text(g, text, UiKit.UI(11.5f), Theme.TextSecondary,
                    new Rectangle(r.X + Theme.S(30), r.Y, r.Width - Theme.S(48), Theme.S(27)), UiKit.L);
                UiKit.Chevron(g, new Rectangle(r.Right - Theme.S(20), r.Y + Theme.S(8), Theme.S(12), Theme.S(12)),
                    1, Theme.TextTertiary);
            }
        }

        // ---- interaction ----------------------------------------------
        private int RowAt(Point p)
        {
            if (!TreeViewport.Contains(p)) return -1;
            for (int i = 0; i < _rows.Count; i++)
            {
                var r = _rows[i];
                if (p.Y >= r.Y && p.Y < r.Y + r.H) return i;
            }
            return -1;
        }

        private int LinkAt(Point p)
        {
            for (int i = 0; i < _links.Count; i++)
                if (_links[i].Key.Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int row = RowAt(e.Location);
            if (row != _hoverRow) { _hoverRow = row; Invalidate(); }
            int link = LinkAt(e.Location);
            if (link != _hoverLink) { _hoverLink = link; Invalidate(); }
            Cursor = (row >= 0 || link >= 0) ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hoverRow = -1;
            _hoverLink = -1;
            Cursor = Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            Focus();

            if (e.Button == MouseButtons.Right)
            {
                ShowRowMenu(e.Location);
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            int link = LinkAt(e.Location);
            if (link >= 0)
            {
                var act = _links[link].Value;
                if (act != null) act();
                return;
            }

            int row = RowAt(e.Location);
            if (row < 0) return;
            var r = _rows[row];
            if (r.Kind == RowKind.Group)
            {
                if (_collapsed.Contains(r.Group)) _collapsed.Remove(r.Group);
                else _collapsed.Add(r.Group);
                Rebuild();
                Invalidate();
                return;
            }
            _selectedId = r.Session.Id;
            Invalidate();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button != MouseButtons.Left) return;
            int row = RowAt(e.Location);
            if (row < 0) return;
            var r = _rows[row];
            if (r.Kind == RowKind.Session) Activate(r.Session);
        }

        private void Activate(SessionDef s)
        {
            if (s == null) return;
            _selectedId = s.Id;
            Invalidate();
            Raise(() => { if (SessionActivated != null) SessionActivated(s); });
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (e.Delta == 0) return;
            _scroll = Math.Max(0, Math.Min(MaxScroll(), _scroll - Math.Sign(e.Delta) * Theme.S(SessionH)));
            Rebuild();
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Enter)
            {
                var s = SelectedSession;
                if (s != null) Activate(s);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Down) { MoveSelection(1); e.Handled = true; }
            else if (e.KeyCode == Keys.Up) { MoveSelection(-1); e.Handled = true; }
        }

        private void MoveSelection(int delta)
        {
            var sessions = new List<SessionDef>();
            foreach (var r in _rows) if (r.Kind == RowKind.Session) sessions.Add(r.Session);
            if (sessions.Count == 0) return;
            int idx = sessions.FindIndex(delegate(SessionDef s) { return s.Id == _selectedId; });
            idx = Math.Max(0, Math.Min(sessions.Count - 1, idx + delta));
            _selectedId = sessions[idx].Id;
            Invalidate();
        }

        private void ShowRowMenu(Point p)
        {
            int row = RowAt(p);
            if (row < 0) return;
            var r = _rows[row];
            if (r.Kind != RowKind.Session) return;
            _selectedId = r.Session.Id;
            Invalidate();
            var s = r.Session;

            var menu = DarkMenu.Create();
            menu.Items.Add(DarkMenu.Item("打开会话", delegate { Activate(s); }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(DarkMenu.Item("编辑会话…", delegate
            {
                if (EditSessionRequested != null) EditSessionRequested(s);
            }));
            menu.Items.Add(DarkMenu.Item("复制会话", delegate
            {
                if (DuplicateSessionRequested != null) DuplicateSessionRequested(s);
                else DuplicateFallback(s);
            }));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(DarkMenu.Item("删除会话", delegate
            {
                if (DeleteSessionRequested != null) DeleteSessionRequested(s);
                else DeleteFallback(s);
            }, null, true));
            menu.Show(this, p);
        }

        private void DuplicateFallback(SessionDef s)
        {
            var c = s.Clone();
            c.Id = Guid.NewGuid().ToString("N");
            c.Name = s.Name + " 副本";
            AppContext.I.Sessions.Add(c);
            AppContext.I.SaveAll();
            SetSessions(AppContext.I.Sessions, null);
        }

        private void DeleteFallback(SessionDef s)
        {
            var res = MessageBox.Show(this, "确定删除会话「" + s.Name + "」？", "删除会话",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
            if (res != DialogResult.OK) return;
            AppContext.I.DeleteSession(s.Id);
            AppContext.I.SaveAll();
            SetSessions(AppContext.I.Sessions, null);
        }
    }
}
