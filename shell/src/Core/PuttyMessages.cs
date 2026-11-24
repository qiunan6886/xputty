using System;
using System.Runtime.InteropServices;
using System.Text;

namespace XPutty
{
    /// <summary>
    /// Glue to the patched putty.exe: registered window messages, macro text
    /// injection (WM_COPYDATA), focus hand-off and close/restart commands.
    /// The putty.exe side of this protocol is implemented in
    /// putty/windows/xputty.c.
    /// </summary>
    public static class PuttyMessages
    {
        public const int WM_CLOSE = 0x0010;
        public const int WM_COMMAND = 0x0111;
        public const int WM_COPYDATA = 0x004A;
        public const int WM_PARENTNOTIFY = 0x0210;
        public const uint XPUTTY_COPYDATA_MAGIC = 0x58505459u; // 'XPTY'
        public const int IDM_RESTART = 0x0040;
        public const uint SMTO_ABORTIFHUNG = 0x0002;

        public static readonly int MsgCreated = RegisterWindowMessage("xPuTTYCreated");
        public static readonly int MsgSessionExit = RegisterWindowMessage("xPuTTYSessionExit");
        public static readonly int MsgHotkey = RegisterWindowMessage("xPuTTYHotkey");
        public static readonly int MsgFatal = RegisterWindowMessage("xPuTTYFatal");

        [StructLayout(LayoutKind.Sequential)]
        public struct COPYDATASTRUCT
        {
            public IntPtr dwData;
            public int cbData;
            public IntPtr lpData;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegisterWindowMessage(string name);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessageTimeout(
            IntPtr hWnd, int msg, IntPtr wParam, ref COPYDATASTRUCT lParam,
            uint flags, uint timeout, out IntPtr result);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam,
                                                 IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam,
                                              IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo,
                                                     bool attach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        /// <summary>
        /// Inject text into a terminal as if it had been typed.  "\r\n" and
        /// "\n" are normalised to CR, which the terminal treats as Enter.
        /// </summary>
        public static bool InjectText(IntPtr child, string text, bool appendEnter)
        {
            if (child == IntPtr.Zero) return false;
            if (text == null) text = "";
            text = text.Replace("\r\n", "\r").Replace("\n", "\r");

            byte[] utf8 = Encoding.UTF8.GetBytes(text);
            int len = 4 + utf8.Length;
            IntPtr buf = Marshal.AllocHGlobal(len);
            try
            {
                Marshal.WriteByte(buf, 0, (byte)(appendEnter ? 1 : 0));
                Marshal.WriteByte(buf, 1, 0);
                Marshal.WriteByte(buf, 2, 0);
                Marshal.WriteByte(buf, 3, 0);
                if (utf8.Length > 0)
                    Marshal.Copy(utf8, 0, new IntPtr(buf.ToInt64() + 4), utf8.Length);

                var cds = new COPYDATASTRUCT
                {
                    dwData = new IntPtr(unchecked((int)XPUTTY_COPYDATA_MAGIC)),
                    cbData = len,
                    lpData = buf
                };
                IntPtr result;
                IntPtr r = SendMessageTimeout(child, WM_COPYDATA, IntPtr.Zero,
                                              ref cds, SMTO_ABORTIFHUNG, 3000,
                                              out result);
                return r != IntPtr.Zero;
            }
            catch
            {
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        /// <summary>Give keyboard focus to a terminal child window.</summary>
        public static void FocusChild(IntPtr child)
        {
            if (child == IntPtr.Zero) return;
            uint pid;
            uint childThread = GetWindowThreadProcessId(child, out pid);
            uint myThread = GetCurrentThreadId();
            if (childThread == myThread)
            {
                SetFocus(child);
                return;
            }
            AttachThreadInput(myThread, childThread, true);
            try { SetFocus(child); }
            finally { AttachThreadInput(myThread, childThread, false); }
        }

        public static void CloseTerminal(IntPtr child)
        {
            if (child != IntPtr.Zero)
                PostMessage(child, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        public static void RestartSession(IntPtr child)
        {
            if (child != IntPtr.Zero)
                PostMessage(child, WM_COMMAND, new IntPtr(IDM_RESTART), IntPtr.Zero);
        }

        /// <summary>
        /// Expand macro variables: $HOST, $USER, $PORT, $NAME, $GROUP.
        /// Also expands \\r / \\n / \\t escape sequences.
        /// </summary>
        public static string ExpandMacro(string text, SessionDef s)
        {
            if (text == null) return "";
            string host = s != null ? s.Host : "";
            string user = s != null ? s.User : "";
            string port = s != null && s.Port > 0 ? s.Port.ToString() : "";
            string name = s != null ? s.Name : "";
            string group = s != null ? s.Group : "";
            text = text
                .Replace("$HOST", host)
                .Replace("$USER", user)
                .Replace("$PORT", port)
                .Replace("$NAME", name)
                .Replace("$GROUP", group);
            text = text.Replace("\\r", "\r").Replace("\\n", "\n").Replace("\\t", "\t");
            return text;
        }
    }
}
