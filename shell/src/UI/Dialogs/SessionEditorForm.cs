using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO.Ports;
using System.Windows.Forms;

namespace XPutty
{
    /// <summary>
    /// New / edit session dialog (640x520): protocol segments, host/port/user/key,
    /// serial port + framing, colour tag and a live command-line preview.
    /// </summary>
    public class SessionEditorForm : ModalForm
    {
        private readonly SessionDef _original;
        private readonly SessionDef _s;

        private SegmentedControl _protocol;
        private FlatInput _name, _group, _host, _user, _key;
        private FlatDropdown _port, _comPort, _baud, _dataBits, _parity, _stopBits;
        private ChromeButton _browse, _scan;
        private ColorDotPicker _colors;
        private PaintPanel _preview;

        private Label _lHost, _lPort, _lUser, _lKey;
        private Label _lCom, _lBaud, _lBits, _lParity, _lStop;
        private Label _hintPort, _hintSerial;

        /// <summary>Result object: the passed instance (written back) or the new one.</summary>
        public SessionDef Session { get { return _original ?? _s; } }
        public SessionDef Result { get { return _s; } }

        public SessionEditorForm(SessionDef s)
        {
            _original = s;
            _s = s != null ? s.Clone() : new SessionDef();

            TitleText = s == null ? "新建会话" : "编辑会话";
            SubtitleText = "SSH / Telnet / 串口 / Raw 连接参数";
            HeaderIconKind = "host";

            SetDialogSize(640, 520);
            BuildUi();
            AddFooterButton("取消", false, delegate { Accepted = false; DialogResult = DialogResult.Cancel; Close(); });
            AddFooterButton("保存", true, SaveClick);
            ApplyRoundedRegion();

            LoadFromModel();
        }

        private void BuildUi()
        {
            const int L = 18, C2 = 334, RW = 604;

            Caption("协议", Theme.S(L), Theme.S(14));
            _protocol = new SegmentedControl();
            _protocol.Options = new[] { "SSH", "Telnet", "串口", "Raw" };
            _protocol.SetBounds(Theme.S(L), Theme.S(31), Theme.S(RW), Theme.S(32));
            _protocol.SelectedIndexChanged += delegate { ApplyProtocol(); UpdatePreview(); };
            Body.Controls.Add(_protocol);

            // --- name / group
            Caption("名称", Theme.S(L), Theme.S(77));
            _name = new FlatInput();
            _name.MaxLength = 40;
            _name.SetBounds(Theme.S(L), Theme.S(94), Theme.S(292), Theme.S(32));
            _name.TextChanged += delegate { UpdatePreview(); };
            Body.Controls.Add(_name);

            Caption("分组", Theme.S(C2), Theme.S(77));
            _group = new FlatInput();
            _group.Placeholder = "留空为未分组";
            _group.MaxLength = 24;
            _group.SetBounds(Theme.S(C2), Theme.S(94), Theme.S(288), Theme.S(32));
            Body.Controls.Add(_group);

            // --- ssh family fields
            _lHost = Caption("主机", Theme.S(L), Theme.S(140));
            _host = new FlatInput();
            _host.Placeholder = "10.0.0.21 或 example.com";
            _host.SetBounds(Theme.S(L), Theme.S(157), Theme.S(292), Theme.S(32));
            _host.TextChanged += delegate { UpdatePreview(); };
            Body.Controls.Add(_host);

            _lPort = Caption("端口", Theme.S(C2), Theme.S(140));
            _port = new FlatDropdown();
            _port.SetItems(new[] { "22", "23", "21", "80", "443", "2222", "8022" });
            _port.SetBounds(Theme.S(C2), Theme.S(157), Theme.S(120), Theme.S(32));
            _port.SelectedIndexChanged += delegate { UpdatePreview(); };
            Body.Controls.Add(_port);
            _hintPort = AddLabel("默认 22 / 23", Theme.S(C2 + 128), Theme.S(157), Theme.S(150), Theme.S(32),
                Theme.TextTertiary, 10.5f);

            _lUser = Caption("用户名", Theme.S(L), Theme.S(203));
            _user = new FlatInput();
            _user.Placeholder = "root / ops";
            _user.SetBounds(Theme.S(L), Theme.S(220), Theme.S(292), Theme.S(32));
            _user.TextChanged += delegate { UpdatePreview(); };
            Body.Controls.Add(_user);

            _lKey = Caption("私钥文件", Theme.S(C2), Theme.S(203));
            _key = new FlatInput();
            _key.Placeholder = "可选：.ppk / .pem";
            _key.SetBounds(Theme.S(C2), Theme.S(220), Theme.S(234), Theme.S(32));
            _key.TextChanged += delegate { UpdatePreview(); };
            Body.Controls.Add(_key);

            _browse = new ChromeButton();
            _browse.Style = ChromeStyle.Flat;
            _browse.Text = "浏览";
            _browse.Font = UiKit.UI(11.5f);
            _browse.PadX = 8;
            _browse.SetBounds(Theme.S(C2 + 242), Theme.S(220), Theme.S(46), Theme.S(32));
            _browse.Click += delegate { BrowseKey(); };
            Body.Controls.Add(_browse);

            // --- serial fields
            _lCom = Caption("串口", Theme.S(L), Theme.S(140));
            _comPort = new FlatDropdown();
            _comPort.SetItems(PortNames());
            _comPort.SetBounds(Theme.S(L), Theme.S(157), Theme.S(200), Theme.S(32));
            _comPort.SelectedIndexChanged += delegate { UpdatePreview(); };
            Body.Controls.Add(_comPort);

            _scan = new ChromeButton();
            _scan.Style = ChromeStyle.Ghost;
            _scan.Icon = "refresh";
            _scan.IconOnly = true;
            _scan.Radius = 4;
            _scan.SetBounds(Theme.S(L + 204), Theme.S(157), Theme.S(44), Theme.S(32));
            _scan.Click += delegate { _comPort.SetItems(PortNames()); };
            Body.Controls.Add(_scan);

            _lBaud = Caption("波特率", Theme.S(C2), Theme.S(140));
            _baud = new FlatDropdown();
            _baud.SetItems(new[] { "9600", "19200", "38400", "57600", "115200", "230400", "460800", "921600" });
            _baud.SetBounds(Theme.S(C2), Theme.S(157), Theme.S(140), Theme.S(32));
            _baud.SelectedIndexChanged += delegate { UpdatePreview(); };
            Body.Controls.Add(_baud);
            _hintSerial = AddLabel("8-N-1", Theme.S(C2 + 148), Theme.S(157), Theme.S(140), Theme.S(32),
                Theme.TextTertiary, 10.5f);

            _lBits = Caption("数据位", Theme.S(L), Theme.S(203));
            _dataBits = new FlatDropdown();
            _dataBits.SetItems(new[] { "8", "7", "6", "5" });
            _dataBits.SetBounds(Theme.S(L), Theme.S(220), Theme.S(140), Theme.S(32));
            _dataBits.SelectedIndexChanged += delegate { UpdatePreview(); };
            Body.Controls.Add(_dataBits);

            _lParity = Caption("校验", Theme.S(170), Theme.S(203));
            _parity = new FlatDropdown();
            _parity.SetItems(new[] { "无 (N)", "奇 (O)", "偶 (E)", "标记 (M)", "空格 (S)" });
            _parity.SetBounds(Theme.S(170), Theme.S(220), Theme.S(140), Theme.S(32));
            _parity.SelectedIndexChanged += delegate { UpdatePreview(); };
            Body.Controls.Add(_parity);

            _lStop = Caption("停止位", Theme.S(322), Theme.S(203));
            _stopBits = new FlatDropdown();
            _stopBits.SetItems(new[] { "1", "1.5", "2" });
            _stopBits.SetBounds(Theme.S(322), Theme.S(220), Theme.S(140), Theme.S(32));
            _stopBits.SelectedIndexChanged += delegate { UpdatePreview(); };
            Body.Controls.Add(_stopBits);

            // --- colour / preview
            Caption("颜色标签", Theme.S(L), Theme.S(286));
            _colors = new ColorDotPicker();
            _colors.SetBounds(Theme.S(L), Theme.S(306), Theme.S(160), Theme.S(24));
            Body.Controls.Add(_colors);

            _preview = new PaintPanel();
            _preview.BackColor = UiKit.DialogBg;
            _preview.Painter = PaintPreview;
            _preview.SetBounds(Theme.S(L), Theme.S(342), Theme.S(RW), Theme.S(50));
            Body.Controls.Add(_preview);
        }

        private static string[] PortNames()
        {
            try
            {
                string[] names = SerialPort.GetPortNames();
                if (names != null && names.Length > 0)
                {
                    Array.Sort(names, delegate(string a, string b)
                    {
                        int ia = 0, ib = 0;
                        int.TryParse((a ?? "").Replace("COM", ""), out ia);
                        int.TryParse((b ?? "").Replace("COM", ""), out ib);
                        return ia.CompareTo(ib);
                    });
                    return names;
                }
            }
            catch { }
            var list = new List<string>();
            for (int i = 1; i <= 8; i++) list.Add("COM" + i);
            return list.ToArray();
        }

        private void PaintPreview(Graphics g, Rectangle r)
        {
            Theme.FillRound(g, r, Theme.S(6), Theme.Blend(UiKit.DialogBg, Theme.Bg0, 0.35f));
            Theme.DrawRound(g, new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), Theme.S(6),
                Theme.BorderSoft, Math.Max(1f, Theme.SF(1f)));
            UiKit.Text(g, "连接命令预览", UiKit.UI(10f), Theme.TextTertiary,
                new Rectangle(r.X + Theme.S(12), r.Y + Theme.S(5), r.Width - Theme.S(24), Theme.S(14)),
                UiKit.L);
            UiKit.Text(g, BuildPreview(), UiKit.Mono(11f), Theme.TextSecondary,
                new Rectangle(r.X + Theme.S(12), r.Y + Theme.S(20), r.Width - Theme.S(24), Theme.S(20)),
                UiKit.LE);
        }

        private string BuildPreview()
        {
            SessionDef s = Collect();
            string q = "\"";
            switch (s.Protocol)
            {
                case ProtocolKind.Ssh:
                    return "putty.exe -ssh -P " + s.Port
                        + (string.IsNullOrEmpty(s.User) ? "" : " -l " + s.User)
                        + (string.IsNullOrEmpty(s.KeyFile) ? "" : " -i " + q + s.KeyFile + q)
                        + " " + Display(s.Host, "host");
                case ProtocolKind.Telnet:
                    return "putty.exe -telnet -P " + s.Port + " " + Display(s.Host, "host");
                case ProtocolKind.Raw:
                    return "putty.exe -raw -P " + s.Port + " " + Display(s.Host, "host");
                default:
                    return "putty.exe -serial " + Display(s.SerialPort, "COMx")
                        + " -sercfg " + s.SerialConfig;
            }
        }

        private static string Display(string v, string fallback)
        {
            return string.IsNullOrEmpty(v) ? "<" + fallback + ">" : v;
        }

        private void BrowseKey()
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title = "选择私钥文件";
                dlg.Filter = "PuTTY 私钥 (*.ppk)|*.ppk|所有文件 (*.*)|*.*";
                dlg.CheckFileExists = true;
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    _key.Text = dlg.FileName;
            }
        }

        private void ApplyProtocol()
        {
            bool serial = _protocol.SelectedIndex == 2;
            _lHost.Visible = _host.Visible = !serial;
            _lPort.Visible = _port.Visible = _hintPort.Visible = !serial;
            _lUser.Visible = _user.Visible = !serial;
            _lKey.Visible = _key.Visible = _browse.Visible = !serial;
            _lCom.Visible = _comPort.Visible = _scan.Visible = serial;
            _lBaud.Visible = _baud.Visible = _hintSerial.Visible = serial;
            _lBits.Visible = _dataBits.Visible = _lParity.Visible = _parity.Visible
                = _lStop.Visible = _stopBits.Visible = serial;
            if (!serial && _protocol.SelectedIndex == 0 && _port.SelectedIndex < 0) _port.Value = "22";
            if (!serial && _protocol.SelectedIndex == 1 && _port.SelectedIndex < 0) _port.Value = "23";
            UpdateHint();
        }

        private void UpdateHint()
        {
            _hintPort.Text = _protocol.SelectedIndex == 1 ? "默认 23" : "默认 22";
            _hintSerial.Text = "流控：无 (N)";
        }

        private void LoadFromModel()
        {
            int proto = (int)_s.Protocol;
            _protocol.SelectedIndex = proto < 0 || proto > 3 ? 0 : proto;
            _name.Text = _s.Name;
            _group.Text = _s.Group;
            _host.Text = _s.Host;
            _user.Text = _s.User;
            _key.Text = _s.KeyFile;

            string port = _s.Port > 0 ? _s.Port.ToString() : "";
            _port.Value = port;
            if (_port.SelectedIndex < 0 && !string.IsNullOrEmpty(port))
            {
                _port.Items.Add(port);
                _port.Value = port;
            }
            if (_port.SelectedIndex < 0) _port.Value = "22";

            // serial "115200,8,n,1,N"
            string[] cfg = (_s.SerialConfig ?? "115200,8,n,1,N").Split(',');
            _comPort.Value = string.IsNullOrEmpty(_s.SerialPort) ? "COM1" : _s.SerialPort;
            if (_comPort.SelectedIndex < 0 && !string.IsNullOrEmpty(_s.SerialPort))
            {
                _comPort.Items.Add(_s.SerialPort);
                _comPort.Value = _s.SerialPort;
            }
            if (_comPort.SelectedIndex < 0 && _comPort.Items.Count > 0) _comPort.SelectedIndex = 0;
            SetDrop(_baud, cfg.Length > 0 ? cfg[0] : "115200");
            SetDrop(_dataBits, cfg.Length > 1 ? cfg[1] : "8");
            string parity = cfg.Length > 2 ? cfg[2].ToLowerInvariant() : "n";
            _parity.SelectedIndex = parity == "o" ? 1 : parity == "e" ? 2 : parity == "m" ? 3 : parity == "s" ? 4 : 0;
            string stop = cfg.Length > 3 ? cfg[3] : "1";
            SetDrop(_stopBits, stop);

            _colors.SelectedColor = string.IsNullOrEmpty(_s.ColorTag) ? "blue" : _s.ColorTag;

            ApplyProtocol();
            UpdatePreview();
        }

        private static void SetDrop(FlatDropdown d, string value)
        {
            d.Value = value;
            if (d.SelectedIndex < 0) { d.Items.Add(value); d.Value = value; }
        }

        private static bool IsNumeric(string s)
        {
            int v;
            return int.TryParse(s, out v);
        }

        /// <summary>Read every field into a SessionDef (used for save and preview).</summary>
        private SessionDef Collect()
        {
            var s = _s.Clone();
            s.Protocol = (ProtocolKind)_protocol.SelectedIndex;
            s.Name = (_name.Text ?? "").Trim();
            s.Group = (_group.Text ?? "").Trim();
            s.Host = (_host.Text ?? "").Trim();
            int port;
            s.Port = int.TryParse((_port.SelectedText ?? "").Trim(), out port) ? port
                   : s.Protocol == ProtocolKind.Telnet ? 23 : s.Protocol == ProtocolKind.Raw ? 0 : 22;
            s.User = (_user.Text ?? "").Trim();
            s.KeyFile = (_key.Text ?? "").Trim();
            s.SerialPort = (_comPort.SelectedText ?? "").Trim();
            string data = string.IsNullOrEmpty(_dataBits.SelectedText) ? "8" : _dataBits.SelectedText;
            string parity = _parity.SelectedIndex == 1 ? "o" : _parity.SelectedIndex == 2 ? "e"
                          : _parity.SelectedIndex == 3 ? "m" : _parity.SelectedIndex == 4 ? "s" : "n";
            string stop = string.IsNullOrEmpty(_stopBits.SelectedText) ? "1" : _stopBits.SelectedText;
            string baud = string.IsNullOrEmpty(_baud.SelectedText) ? "115200" : _baud.SelectedText;
            s.SerialConfig = baud + "," + data + "," + parity + "," + stop + ",N";
            s.ColorTag = string.IsNullOrEmpty(_colors.SelectedColor) ? "blue" : _colors.SelectedColor;
            if (string.IsNullOrEmpty(s.Name))
            {
                s.Name = s.Protocol == ProtocolKind.Serial
                    ? Display(s.SerialPort, "串口")
                    : Display(s.Host, "新会话");
            }
            return s;
        }

        private void UpdatePreview()
        {
            if (_preview != null) _preview.Invalidate();
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
            SessionDef s = Collect();
            // write back into the working copy
            _s.Name = s.Name;
            _s.Group = s.Group;
            _s.Protocol = s.Protocol;
            _s.Host = s.Host;
            _s.Port = s.Port;
            _s.User = s.User;
            _s.KeyFile = s.KeyFile;
            _s.SerialPort = s.SerialPort;
            _s.SerialConfig = s.SerialConfig;
            _s.ColorTag = s.ColorTag;
            if (_original != null)
            {
                _original.Name = _s.Name;
                _original.Group = _s.Group;
                _original.Protocol = _s.Protocol;
                _original.Host = _s.Host;
                _original.Port = _s.Port;
                _original.User = _s.User;
                _original.KeyFile = _s.KeyFile;
                _original.SerialPort = _s.SerialPort;
                _original.SerialConfig = _s.SerialConfig;
                _original.ColorTag = _s.ColorTag;
            }
            Accepted = true;
            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
