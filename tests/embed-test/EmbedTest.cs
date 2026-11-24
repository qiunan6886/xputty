using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

class EmbedTest
{
    const int WM_COPYDATA = 0x004A;
    const uint SMTO_ABORTIFHUNG = 0x0002;
    const uint PW_RENDERFULLCONTENT = 2;

    [StructLayout(LayoutKind.Sequential)]
    struct COPYDATASTRUCT { public IntPtr dwData; public int cbData; public IntPtr lpData; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")]
    static extern IntPtr SendMessageTimeout(IntPtr hWnd, int msg, IntPtr wParam, ref COPYDATASTRUCT lParam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")]
    static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")]
    static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")]
    static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")]
    static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")]
    static extern IntPtr SetFocus(IntPtr hWnd);
    [DllImport("user32.dll")]
    static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("kernel32.dll")]
    static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    static uint msgCreated, msgExit, msgHotkey, msgFatal;
    static Form form;
    static Panel panel;
    public delegate void MsgHandler(ref Message m);
    static MsgHandler handler;

    class TestForm : Form
    {
        public MsgHandler Callback;
        protected override void WndProc(ref Message m)
        {
            if (Callback != null) Callback(ref m);
            base.WndProc(ref m);
        }
    }
    static IntPtr childHwnd = IntPtr.Zero;
    static string lastFatal = null;
    static int exitCode = -999;
    static int frameW = 1000, frameH = 700;
    static string outPng = @"D:\workspace\xputty\build\embed-test.png";
    static Process putty;
    static TcpListener listener;

    static void Log(string s) { Console.WriteLine("[test] " + s); }

    [STAThread]
    static void Main(string[] args)
    {
        string puttyPath = args.Length > 0 ? args[0] : @"D:\workspace\xputty\build\putty-lead\putty.exe";
        string themePath = args.Length > 1 ? args[1] : @"D:\workspace\xputty\build\test-theme.ini";
        if (args.Length > 2) outPng = args[2];

        msgCreated = RegisterWindowMessage("xPuTTYCreated");
        msgExit = RegisterWindowMessage("xPuTTYSessionExit");
        msgHotkey = RegisterWindowMessage("xPuTTYHotkey");
        msgFatal = RegisterWindowMessage("xPuTTYFatal");
        Log("messages created=" + msgCreated + " exit=" + msgExit + " hotkey=" + msgHotkey + " fatal=" + msgFatal);

        // Local echo server: raw PuTTY will connect here.
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptThread = new Thread(() =>
        {
            try
            {
                var client = listener.AcceptTcpClient();
                var s = client.GetStream();
                var buf = new byte[4096];
                while (true)
                {
                    int n = s.Read(buf, 0, buf.Length);
                    if (n <= 0) break;
                    s.Write(buf, 0, n);   // echo back so injected text is visible
                }
            }
            catch { }
        });
        acceptThread.IsBackground = true;
        acceptThread.Start();
        Log("echo server on port " + port);

        form = new TestForm();
        ((TestForm)form).Callback = HandleMessage;
        form.Text = "xPuTTY embed test";
        form.Width = frameW;
        form.Height = frameH;
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(40, 40);
        panel = new Panel();
        panel.Dock = DockStyle.Fill;
        panel.BackColor = Color.FromArgb(15, 17, 21);
        form.Controls.Add(panel);

        form.Shown += (s, e) =>
        {
            string themeArg = File.Exists(themePath) ? " -xputty-theme \"" + themePath + "\"" : "";
            string cmd = "-xputty-embed " + panel.Handle.ToInt64() +
                         " -xputty-host " + form.Handle.ToInt64() +
                         themeArg +
                         " -P " + port + " -raw 127.0.0.1";
            Log("launch: " + puttyPath + " " + cmd);
            putty = Process.Start(new ProcessStartInfo(puttyPath, cmd) { UseShellExecute = false });
            Log("putty pid=" + putty.Id);

            var t = new System.Windows.Forms.Timer();
            t.Interval = 2500;
            t.Tick += (s2, e2) =>
            {
                t.Stop();
                Inspect();
                InjectMacro("echo xputty-inject-ok");
                var t2 = new System.Windows.Forms.Timer();
                t2.Interval = 2000;
                t2.Tick += (s3, e3) =>
                {
                    t2.Stop();
                    Capture();
                    Finish();
                };
                t2.Start();
            };
            t.Start();
        };

        Application.Run(form);
    }

    static void HandleMessage(ref Message m)
    {
        if ((uint)m.Msg == msgCreated)
        {
            childHwnd = m.WParam;
            Log("created child hwnd=" + childHwnd);
            FocusChild();
        }
        else if ((uint)m.Msg == msgExit)
        {
            exitCode = (int)m.WParam;
            Log("exit notification code=" + exitCode);
        }
        else if ((uint)m.Msg == msgHotkey)
        {
            Log("hotkey notification vk=" + m.WParam + " shift=" + m.LParam);
        }
        else if ((uint)m.Msg == msgFatal)
        {
            lastFatal = Marshal.PtrToStringAnsi(m.LParam);
            Log("fatal notification: " + lastFatal);
        }
    }

    static void FocusChild()
    {
        if (childHwnd == IntPtr.Zero) return;
        uint childPid, childThread;
        childThread = GetWindowThreadProcessId(childHwnd, out childPid);
        uint myThread = GetCurrentThreadId();
        AttachThreadInput(myThread, childThread, true);
        SetFocus(childHwnd);
        AttachThreadInput(myThread, childThread, false);
        Log("focused child");
    }

    static void Inspect()
    {
        Log("--- inspect ---");
        Log("child hwnd: " + childHwnd);
        if (putty != null)
        {
            try
            {
                if (putty.HasExited) Log("putty process EXITED code=" + putty.ExitCode);
                else Log("putty process alive, pid=" + putty.Id);
            }
            catch (Exception ex) { Log("putty state error: " + ex.Message); }
        }
        Log("panel children count=" + PanelChildCount());
        if (childHwnd == IntPtr.Zero) return;
        var sb = new StringBuilder(256);
        GetClassName(childHwnd, sb, sb.Capacity);
        Log("child class: " + sb);
        long style = GetWindowLongPtr(childHwnd, -16).ToInt64();
        Log("style=0x" + style.ToString("X") + " WS_CHILD=" + ((style & 0x40000000L) != 0) + " WS_VISIBLE=" + ((style & 0x10000000L) != 0) + " WS_CAPTION=" + ((style & 0x00C00000L) != 0));
        RECT wr, pr; GetWindowRect(childHwnd, out wr); GetClientRect(panel.Handle, out pr);
        Log("child rect: " + wr.Left + "," + wr.Top + "," + (wr.Right - wr.Left) + "x" + (wr.Bottom - wr.Top) + "  panel client: " + pr.Right + "x" + pr.Bottom);
        uint pid; GetWindowThreadProcessId(childHwnd, out pid);
        Log("child pid=" + pid);
        if (putty != null)
        {
            try
            {
                if (putty.HasExited) Log("putty process EXITED code=" + putty.ExitCode);
                else Log("putty process alive");
            }
            catch (Exception ex) { Log("putty state error: " + ex.Message); }
        }
        Log("panel children count=" + PanelChildCount());
    }

    [DllImport("user32.dll")]
    static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lParam);
    delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    static int panelChildCount;
    static bool CountChild(IntPtr hwnd, IntPtr lParam) { panelChildCount++; return true; }
    static int PanelChildCount()
    {
        panelChildCount = 0;
        if (panel != null) EnumChildWindows(panel.Handle, CountChild, IntPtr.Zero);
        return panelChildCount;
    }

    static void InjectMacro(string text)
    {
        if (childHwnd == IntPtr.Zero) { Log("inject skipped: no child"); return; }
        byte[] utf8 = Encoding.UTF8.GetBytes(text);
        int len = 4 + utf8.Length;
        IntPtr buf = Marshal.AllocHGlobal(len);
        Marshal.WriteByte(buf, 0, 1); // flags: append CR
        Marshal.WriteByte(buf, 1, 0);
        Marshal.WriteByte(buf, 2, 0);
        Marshal.WriteByte(buf, 3, 0);
        Marshal.Copy(utf8, 0, new IntPtr(buf.ToInt64() + 4), utf8.Length);
        COPYDATASTRUCT cds;
        cds.dwData = (IntPtr)0x58505459;
        cds.cbData = len;
        cds.lpData = buf;
        IntPtr result;
        IntPtr r = SendMessageTimeout(childHwnd, WM_COPYDATA, form.Handle, ref cds, SMTO_ABORTIFHUNG, 3000, out result);
        Marshal.FreeHGlobal(buf);
        Log("inject returned " + r + " text=" + text);
    }

    static void Capture()
    {
        try
        {
            var bmp = new Bitmap(form.Width, form.Height);
            using (var g = Graphics.FromImage(bmp))
            {
                bool ok = false;
                try
                {
                    g.CopyFromScreen(form.Location, Point.Empty, form.Size);
                    ok = true;
                }
                catch (Exception ex) { Log("CopyFromScreen failed: " + ex.Message); }
                if (!ok)
                {
                    IntPtr hdc = g.GetHdc();
                    PrintWindow(form.Handle, hdc, PW_RENDERFULLCONTENT);
                    g.ReleaseHdc(hdc);
                }
            }
            bmp.Save(outPng, ImageFormat.Png);
            Log("screenshot saved: " + outPng);
        }
        catch (Exception ex) { Log("capture failed: " + ex.Message); }
    }

    static void Finish()
    {
        Log("--- finishing ---");
        if (childHwnd != IntPtr.Zero)
        {
            Log("sending WM_CLOSE to child");
            SendMessage(childHwnd, 0x0010, IntPtr.Zero, IntPtr.Zero);
        }
        var timer = new System.Windows.Forms.Timer();
        timer.Interval = 1200;
        timer.Tick += (s, e) =>
        {
            timer.Stop();
            if (putty != null && !putty.HasExited)
            {
                try { putty.Kill(); } catch { }
            }
            Log("done");
            form.Close();
        };
        timer.Start();
    }
}

