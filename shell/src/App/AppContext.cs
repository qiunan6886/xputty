using System;
using System.Collections.Generic;
using System.Drawing.Text;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace XPutty
{
    public enum ProtocolKind { Ssh, Telnet, Serial, Raw }

    [DataContract]
    public class SessionDef
    {
        [DataMember] public string Id = Guid.NewGuid().ToString("N");
        [DataMember] public string Name = "";
        [DataMember] public string Group = "";
        [DataMember] public ProtocolKind Protocol = ProtocolKind.Ssh;
        [DataMember] public string Host = "";
        [DataMember] public int Port = 22;
        [DataMember] public string User = "";
        [DataMember] public string KeyFile = "";
        [DataMember] public string SerialPort = "COM1";
        [DataMember] public string SerialConfig = "115200,8,n,1,N";
        [DataMember] public string ColorTag = "blue";
        [DataMember] public string TerminalTheme = "default";
        [DataMember] public bool Pin = false;

        public bool IsSerial { get { return Protocol == ProtocolKind.Serial; } }

        public string Subtitle
        {
            get
            {
                if (Protocol == ProtocolKind.Serial)
                    return SerialPort + " · " + SerialConfig.Replace(",", " ");
                return Host + (Port > 0 ? ":" + Port : "");
            }
        }

        public SessionDef Clone()
        {
            return (SessionDef)MemberwiseClone();
        }
    }

    [DataContract]
    public class MacroDef
    {
        [DataMember] public string Id = Guid.NewGuid().ToString("N");
        [DataMember] public string Name = "新宏";
        [DataMember] public string Text = "";
        [DataMember] public string Hotkey = "";
        [DataMember] public string Color = "blue";
        [DataMember] public bool SendEnter = true;
        [DataMember] public bool Enabled = true;
        [DataMember] public int Sort = 0;
        [DataMember] public string Scope = "global";

        public MacroDef Clone()
        {
            return (MacroDef)MemberwiseClone();
        }
    }

    [DataContract]
    public class AppSettings
    {
        [DataMember] public string Theme = "dark";
        [DataMember] public string Accent = "#4C8DFF";
        [DataMember] public int FontSize = 10;
        [DataMember] public string FontName = "Cascadia Mono";
        [DataMember] public bool ShowSidebar = true;
        [DataMember] public bool ShowMacroBar = true;
        [DataMember] public bool RestoreLastLayout = false;
        [DataMember] public string CloseBehavior = "ask";
        [DataMember] public int WindowX = int.MinValue;
        [DataMember] public int WindowY = int.MinValue;
        [DataMember] public int WindowW = -1;
        [DataMember] public int WindowH = -1;
        [DataMember] public int WindowState = 0;
    }

    internal static class Json
    {
        public static void Save<T>(string path, T value)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
            {
                var ser = new DataContractJsonSerializer(typeof(T));
                ser.WriteObject(fs, value);
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        public static T Load<T>(string path, T fallback) where T : class
        {
            try
            {
                if (!File.Exists(path)) return fallback;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read))
                {
                    var ser = new DataContractJsonSerializer(typeof(T));
                    return ser.ReadObject(fs) as T ?? fallback;
                }
            }
            catch
            {
                return fallback;
            }
        }
    }

    public sealed class AppContext
    {
        private static readonly AppContext _i = new AppContext();
        public static AppContext I { get { return _i; } }

        public AppSettings Settings = new AppSettings();
        public List<SessionDef> Sessions = new List<SessionDef>();
        public List<MacroDef> Macros = new List<MacroDef>();
        public string DataDir;
        public string PuttyExe;
        public string ThemeIniPath;
        public MainForm Main;

        public string SettingsPath { get { return Path.Combine(DataDir, "settings.json"); } }
        public string SessionsPath { get { return Path.Combine(DataDir, "sessions.json"); } }
        public string MacrosPath { get { return Path.Combine(DataDir, "macros.json"); } }

        private AppContext()
        {
            DataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "xPuTTY");
            Directory.CreateDirectory(DataDir);
            var exeDir = Path.GetDirectoryName(typeof(AppContext).Assembly.Location);
            PuttyExe = Path.Combine(exeDir, "putty.exe");
            if (!File.Exists(PuttyExe))
            {
                // Development fallback: the fork build output directory.
                var dev = Path.GetFullPath(Path.Combine(exeDir, "..", "putty-lead", "putty.exe"));
                if (File.Exists(dev)) PuttyExe = dev;
            }
            ThemeIniPath = Path.Combine(DataDir, "themes", "current.ini");
        }

        public void Load()
        {
            Settings = Json.Load(SettingsPath, new AppSettings());
            Sessions = Json.Load(SessionsPath, new List<SessionDef>());
            Macros = Json.Load(MacrosPath, new List<MacroDef>());
            if (Macros.Count == 0)
                Macros = DefaultMacros();
            if (Sessions.Count == 0)
                Sessions = DefaultSessions();
            ReindexMacros();
        }

        public void SaveAll()
        {
            try
            {
                Json.Save(SettingsPath, Settings);
                Json.Save(SessionsPath, Sessions);
                Json.Save(MacrosPath, Macros);
            }
            catch { /* best effort */ }
        }

        private static List<MacroDef> DefaultMacros()
        {
            var colors = new[] { "blue", "red", "green", "yellow", "purple", "teal", "orange", "cyan" };
            var list = new List<MacroDef>();
            var defs = new[] {
                new[] { "查看日志", "docker logs -f --tail 50 $(docker ps --format '{{.Names}}' | head -1)\r" },
                new[] { "重启服务", "sudo systemctl restart nginx && systemctl status nginx --no-pager\r" },
                new[] { "磁盘占用", "df -hT | grep -v tmpfs\r" },
                new[] { "网络诊断", "ss -tlnp | head -20\r" },
                new[] { "内存进程", "free -h && ps aux --sort=-%mem | head -8\r" },
                new[] { "系统巡检", "uptime && uname -a && who\r" },
                new[] { "部署最新", "cd /opt/app && ./deploy.sh --latest\r" },
                new[] { "清理临时", "sudo find /tmp -type f -mtime +7 -delete\r" },
            };
            for (int i = 0; i < defs.Length; i++)
            {
                var m = new MacroDef();
                m.Name = defs[i][0];
                m.Text = defs[i][1];
                m.Color = colors[i % colors.Length];
                m.SendEnter = false; // text already ends with \r
                m.Sort = i;
                m.Hotkey = i < 9 ? "Ctrl+Shift+" + (i + 1) : "";
                list.Add(m);
            }
            return list;
        }

        private static List<SessionDef> DefaultSessions()
        {
            var list = new List<SessionDef>
            {
                NewDemo("prod-web-01", "生产环境", ProtocolKind.Ssh, "10.0.0.21", 22, "ops", "blue"),
                NewDemo("prod-db-02", "生产环境", ProtocolKind.Ssh, "10.0.0.22", 22, "ops", "blue"),
                NewDemo("prod-cache-03", "生产环境", ProtocolKind.Ssh, "10.0.0.23", 22, "ops", "blue"),
                NewDemo("test-web-01", "测试环境", ProtocolKind.Ssh, "10.0.10.21", 22, "root", "yellow"),
                NewDemo("test-db-01", "测试环境", ProtocolKind.Ssh, "10.0.10.22", 22, "root", "yellow"),
                NewDemo("串口-传感器网关", "本地串口", ProtocolKind.Serial, "", 0, "", "teal"),
                NewDemo("串口-工业电表", "本地串口", ProtocolKind.Serial, "", 0, "", "teal"),
            };
            list[5].SerialPort = "COM3";
            list[6].SerialPort = "COM5";
            list[6].SerialConfig = "9600,8,n,1,N";
            return list;
        }

        private static SessionDef NewDemo(string name, string group, ProtocolKind p,
                                          string host, int port, string user, string color)
        {
            var s = new SessionDef();
            s.Name = name;
            s.Group = group;
            s.Protocol = p;
            s.Host = host;
            s.Port = port;
            s.User = user;
            s.ColorTag = color;
            return s;
        }

        public void ReindexMacros()
        {
            Macros.Sort((a, b) => a.Sort.CompareTo(b.Sort));
            for (int i = 0; i < Macros.Count; i++) Macros[i].Sort = i;
        }

        public MacroDef NewMacro()
        {
            var m = new MacroDef();
            m.Sort = Macros.Count;
            m.Name = "新宏 " + (Macros.Count + 1);
            m.Hotkey = Macros.Count < 9 ? "Ctrl+Shift+" + (Macros.Count + 1) : "";
            Macros.Add(m);
            ReindexMacros();
            return m;
        }

        public void DeleteMacro(string id)
        {
            Macros.RemoveAll(m => m.Id == id);
            ReindexMacros();
        }

        public void MoveMacro(string id, int newIndex)
        {
            int old = Macros.FindIndex(x => x.Id == id);
            if (old < 0) return;
            var m = Macros[old];
            Macros.RemoveAt(old);
            newIndex = Math.Max(0, Math.Min(Macros.Count, newIndex));
            Macros.Insert(newIndex, m);
            ReindexMacros();
        }

        public SessionDef NewSession()
        {
            var s = new SessionDef();
            s.Name = "新会话 " + (Sessions.Count + 1);
            Sessions.Add(s);
            return s;
        }

        public void DeleteSession(string id)
        {
            Sessions.RemoveAll(s => s.Id == id);
        }

        /// <summary>Pick an installed monospace font, preferring the design's font.</summary>
        public static string ResolveTerminalFont(string wanted)
        {
            var names = new List<string>();
            try
            {
                using (var col = new InstalledFontCollection())
                    foreach (var f in col.Families) names.Add(f.Name);
            }
            catch { }
            if (!string.IsNullOrEmpty(wanted) && names.Contains(wanted)) return wanted;
            foreach (var cand in new[] { "Cascadia Mono", "Cascadia Code", "Consolas",
                                         "JetBrains Mono", "Courier New" })
                if (names.Contains(cand)) return cand;
            return "Courier New";
        }

        /// <summary>
        /// Write the terminal theme INI consumed by the patched putty.exe.
        /// Colour values come from the design tokens, so the shell chrome and
        /// the terminal stay visually consistent.
        /// </summary>
        public string BuildThemeIni()
        {
            var dir = Path.GetDirectoryName(ThemeIniPath);
            Directory.CreateDirectory(dir);
            var ansi = Theme.Ansi16;
            var sb = new StringBuilder();
            sb.AppendLine("; generated by xPuTTY - do not edit");
            sb.AppendLine("[terminal]");
            sb.AppendLine("font=" + ResolveTerminalFont(Settings.FontName));
            sb.AppendLine("font-size=" + Settings.FontSize);
            sb.AppendLine("scrollback=8000");
            sb.AppendLine("cursor=block");
            sb.AppendLine("bell=off");
            sb.AppendLine("[colors]");
            sb.AppendLine("foreground=#C3CCD9");
            sb.AppendLine("foreground-bold=#EDF1F7");
            sb.AppendLine("background=#0B0D11");
            sb.AppendLine("background-bold=#161A21");
            sb.AppendLine("cursor-text=#0B0D11");
            sb.AppendLine("cursor-colour=#C3CCD9");
            for (int i = 0; i < 16; i++)
                sb.AppendLine("ansi" + i + "=" + Theme.ToHex(ansi[i]));
            File.WriteAllText(ThemeIniPath, sb.ToString(), new UTF8Encoding(false));
            return ThemeIniPath;
        }

        /// <summary>
        /// Import sessions from the user's PuTTY registry store.  Returns the
        /// number of newly imported sessions.
        /// </summary>
        public int ImportPuttySessions()
        {
            int added = 0;
            try
            {
                using (var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\SimonTatham\PuTTY\Sessions"))
                {
                    if (root == null) return 0;
                    foreach (var name in root.GetSubKeyNames())
                    {
                        if (name.StartsWith("xputty", StringComparison.OrdinalIgnoreCase))
                            continue;
                        using (var k = root.OpenSubKey(name))
                        {
                            if (k == null) continue;
                            var host = k.GetValue("HostName") as string;
                            if (string.IsNullOrEmpty(host)) continue;
                            if (Sessions.Exists(x => string.Equals(x.Host, host,
                                    StringComparison.OrdinalIgnoreCase) && x.Name == name))
                                continue;
                            var s = new SessionDef();
                            s.Name = name;
                            s.Host = host;
                            s.Group = "从 PuTTY 导入";
                            int proto = 0;
                            int.TryParse(k.GetValue("Protocol") as string, out proto);
                            s.Protocol = proto == 1 ? ProtocolKind.Telnet
                                       : proto == 4 ? ProtocolKind.Serial
                                       : proto == 5 ? ProtocolKind.Raw
                                       : ProtocolKind.Ssh;
                            int port = 22;
                            int.TryParse(k.GetValue("PortNumber") as string, out port);
                            s.Port = port;
                            s.User = (k.GetValue("UserName") as string) ?? "";
                            s.KeyFile = (k.GetValue("PublicKeyFile") as string) ?? "";
                            Sessions.Add(s);
                            added++;
                        }
                    }
                }
                if (added > 0) SaveAll();
            }
            catch { }
            return added;
        }

        public void OpenSession(SessionDef s, bool newTab = true)
        {
            if (Main != null) Main.OpenSessionInTab(s, newTab);
        }

        public void SendMacro(MacroDef m)
        {
            if (Main != null) Main.SendMacroToActivePane(m);
        }
    }
}
