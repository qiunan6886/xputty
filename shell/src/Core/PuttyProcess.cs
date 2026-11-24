using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace XPutty
{
    /// <summary>
    /// One embedded putty.exe session.
    ///
    /// Command line (see SHELL_SPEC §4):
    ///   putty.exe -xputty-embed &lt;panePanel.Handle&gt; -xputty-host &lt;MainForm.Handle&gt;
    ///             -xputty-theme "&lt;theme.ini&gt;" &lt;connection args&gt;
    ///
    /// stdout/stderr are never redirected: the child is a GUI process and
    /// inheriting the (non-existent) console is harmless, whereas a pipe that
    /// nobody drains could block the terminal.
    /// </summary>
    public sealed class PuttyProcess
    {
        public Process Process { get; private set; }
        public IntPtr PuttyHwnd;
        public readonly PaneView Pane;
        public readonly SessionDef Session;
        public readonly IntPtr PaneHandle;
        public readonly IntPtr HostHandle;

        public bool Started;
        public bool Closing;
        public string LastError;
        public int ExitCode = int.MinValue;

        /// <summary>Every live session, so the shell can reap them on exit.</summary>
        private static readonly List<PuttyProcess> Live = new List<PuttyProcess>();

        private System.Threading.Timer _killTimer;

        public PuttyProcess(PaneView pane, SessionDef session, IntPtr paneHandle, IntPtr hostHandle)
        {
            Pane = pane;
            Session = session;
            PaneHandle = paneHandle;
            HostHandle = hostHandle;
        }

        public bool IsAlive
        {
            get
            {
                try { return Process != null && !Process.HasExited; }
                catch { return false; }
            }
        }

        public int Pid
        {
            get
            {
                try { return Process != null ? Process.Id : 0; }
                catch { return 0; }
            }
        }

        public bool Attached { get { return PuttyHwnd != IntPtr.Zero; } }

        // ------------------------------------------------------------------
        // Command line
        // ------------------------------------------------------------------

        /// <summary>Quote one argument Windows-style (only when needed).</summary>
        public static string Quote(string value)
        {
            if (value == null) value = "";
            if (value.Length == 0) return "\"\"";
            if (value.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return value;
            var sb = new StringBuilder("\"");
            int backslashes = 0;
            foreach (char c in value)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (c == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                }
                else
                {
                    sb.Append('\\', backslashes);
                    sb.Append(c);
                }
                backslashes = 0;
            }
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        /// <summary>
        /// PuTTY reads its per-launch defaults from a registry session when
        /// started with -load.  The fork forces most embedded-mode options in
        /// xputty.c, but the scrollbar and the sunken border can only be
        /// switched off through the settings store, so the shell maintains a
        /// private "xputty-embedded" session (the PuTTY import skips names
        /// starting with "xputty").
        /// </summary>
        public const string EmbeddedSessionName = "xputty-embedded";

        private static bool _embeddedSessionEnsured;

        public static void EnsureEmbeddedSession()
        {
            if (_embeddedSessionEnsured) return;
            _embeddedSessionEnsured = true;
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                    @"Software\SimonTatham\PuTTY\Sessions\" + EmbeddedSessionName))
                {
                    if (key == null) return;
                    // PuTTY stores integer settings as REG_DWORD strings are
                    // ignored by read_setting_i().
                    SetDword(key, "ScrollBar", 0);
                    SetDword(key, "WarnOnClose", 0);
                    SetDword(key, "SunkenEdge", 0);
                    SetDword(key, "AlwaysOnTop", 0);
                    SetDword(key, "WindowBorder", 0);
                    SetDword(key, "ScrollBarInFullscreen", 0);
                    SetString(key, "TerminalType", "xterm-256color");
                }
            }
            catch { /* registry unavailable: fall back to PuTTY defaults */ }
        }

        private static void SetDword(Microsoft.Win32.RegistryKey key, string name, int value)
        {
            try { key.SetValue(name, value, Microsoft.Win32.RegistryValueKind.DWord); }
            catch { }
        }

        private static void SetString(Microsoft.Win32.RegistryKey key, string name, string value)
        {
            try
            {
                object current = key.GetValue(name);
                if (current is string && (string)current == value) return;
                key.SetValue(name, value, Microsoft.Win32.RegistryValueKind.String);
            }
            catch { }
        }

        /// <summary>Build the complete argument string for one embedded session.</summary>
        public static string BuildArguments(SessionDef s, IntPtr pane, IntPtr host, string themeIni)
        {
            EnsureEmbeddedSession();
            var sb = new StringBuilder(256);
            sb.Append("-xputty-embed ").Append(pane.ToInt64());
            sb.Append(" -xputty-host ").Append(host.ToInt64());
            if (!string.IsNullOrEmpty(themeIni))
                sb.Append(" -xputty-theme ").Append(Quote(themeIni));
            sb.Append(" -load ").Append(EmbeddedSessionName);

            if (s == null) return sb.ToString();

            if (s.Protocol == ProtocolKind.Serial)
            {
                sb.Append(" -serial ").Append(Quote(
                    string.IsNullOrEmpty(s.SerialPort) ? "COM1" : s.SerialPort));
                if (!string.IsNullOrEmpty(s.SerialConfig))
                    sb.Append(" -sercfg ").Append(Quote(s.SerialConfig));
            }
            else
            {
                string proto = s.Protocol == ProtocolKind.Telnet ? "-telnet"
                             : s.Protocol == ProtocolKind.Raw ? "-raw"
                             : "-ssh";
                int port = s.Port;
                if (port <= 0) port = s.Protocol == ProtocolKind.Telnet ? 23 : 22;
                if (s.Protocol == ProtocolKind.Raw && s.Port <= 0) port = s.Port;
                sb.Append(' ').Append(proto).Append(" -P ").Append(port);
                if (!string.IsNullOrEmpty(s.User))
                    sb.Append(" -l ").Append(Quote(s.User));
                if (!string.IsNullOrEmpty(s.KeyFile))
                    sb.Append(" -i ").Append(Quote(s.KeyFile));
                sb.Append(' ').Append(Quote(
                    string.IsNullOrEmpty(s.Host) ? "127.0.0.1" : s.Host));
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------
        // Lifetime
        // ------------------------------------------------------------------

        public bool Start()
        {
            if (Started) return true;
            Started = true;

            string exe = AppContext.I.PuttyExe;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                LastError = "找不到 putty.exe：" + (exe ?? "(空)");
                Diag.Log("putty exe missing: " + (exe ?? "(null)"));
                return false;
            }

            string args = BuildArguments(Session, PaneHandle, HostHandle,
                                         AppContext.I.ThemeIniPath);

            try
            {
                var psi = new ProcessStartInfo(exe, args);
                // No stdout/stderr redirection: the terminal must never block
                // because a pipe buffer filled up.
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                string dir = Path.GetDirectoryName(exe);
                if (!string.IsNullOrEmpty(dir)) psi.WorkingDirectory = dir;

                string dbg = Environment.GetEnvironmentVariable("XPUTTY_DEBUG");
                if (!string.IsNullOrEmpty(dbg))
                {
                    try { psi.EnvironmentVariables["XPUTTY_DEBUG"] = dbg; }
                    catch { /* environment block unavailable */ }
                }

                Diag.Log("putty start: " + exe + " " + args);
                var p = new Process();
                p.StartInfo = psi;
                p.EnableRaisingEvents = true;
                p.Exited += OnProcessExited;
                p.Start();
                Process = p;
                lock (Live) Live.Add(this);
                Diag.Log("putty started pid=" + p.Id);
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Diag.Log("putty start FAILED: " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private void OnProcessExited(object sender, EventArgs e)
        {
            try { ExitCode = Process.ExitCode; }
            catch { }
            lock (Live) Live.Remove(this);

            PaneView pane = Pane;
            if (pane == null || pane.IsDisposed) return;
            try
            {
                if (pane.IsHandleCreated)
                    pane.BeginInvoke(new Action(() => pane.OnPuttyProcessExited(this)));
            }
            catch { /* pane already gone */ }
        }

        /// <summary>Called when the terminal window was created inside our pane.</summary>
        public void AttachWindow(IntPtr hwnd)
        {
            PuttyHwnd = hwnd;
            Closing = false;
        }

        // ------------------------------------------------------------------
        // Input / control
        // ------------------------------------------------------------------

        public bool InjectText(string text, bool appendEnter)
        {
            if (PuttyHwnd == IntPtr.Zero) return false;
            return PuttyMessages.InjectText(PuttyHwnd, text, appendEnter);
        }

        public void Focus()
        {
            if (PuttyHwnd != IntPtr.Zero) PuttyMessages.FocusChild(PuttyHwnd);
        }

        public void Restart()
        {
            if (PuttyHwnd != IntPtr.Zero)
                PuttyMessages.RestartSession(PuttyHwnd);
        }

        /// <summary>Ask the terminal to close, then force-kill if it lingers.</summary>
        public void Close()
        {
            Closing = true;
            if (PuttyHwnd != IntPtr.Zero && IsWindow(PuttyHwnd))
                PuttyMessages.CloseTerminal(PuttyHwnd);
            else if (IsAlive)
                Kill();

            ScheduleKill(1500);
        }

        public void Kill()
        {
            try
            {
                if (Process != null && !Process.HasExited) Process.Kill();
            }
            catch { }
        }

        private void ScheduleKill(int milliseconds)
        {
            if (_killTimer != null) return;
            _killTimer = new System.Threading.Timer(delegate
            {
                try
                {
                    if (IsAlive) Kill();
                }
                catch { }
                var t = _killTimer;
                _killTimer = null;
                if (t != null) t.Dispose();
            }, null, milliseconds, Timeout.Infinite);
        }

        public void Release()
        {
            lock (Live) Live.Remove(this);
            if (_killTimer != null)
            {
                try { _killTimer.Dispose(); } catch { }
                _killTimer = null;
            }
            if (Process != null)
            {
                try { Process.Exited -= OnProcessExited; } catch { }
                try { Process.Dispose(); } catch { }
                Process = null;
            }
            PuttyHwnd = IntPtr.Zero;
        }

        public static void KillAll()
        {
            List<PuttyProcess> copy;
            lock (Live) copy = new List<PuttyProcess>(Live);
            foreach (var p in copy)
            {
                try { p.Kill(); } catch { }
                try { p.Release(); } catch { }
            }
            lock (Live) Live.Clear();
        }

        // ------------------------------------------------------------------
        // win32 helpers
        // ------------------------------------------------------------------

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hwnd);
    }
}
