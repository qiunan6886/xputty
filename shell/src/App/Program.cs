using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace XPutty
{
    /// <summary>
    /// Entry point for the xPuTTY shell (xputty.exe).
    /// Single instance, per-monitor DPI aware (see app.manifest), global
    /// exception guard, and the boot sequence AppContext -> theme ini -> MainForm.
    /// </summary>
    internal static class Program
    {
        private const string MutexName = "xPuTTY.Shell.SingleInstance.v1";

        private static Mutex _mutex;

        [STAThread]
        private static void Main(string[] args)
        {
            bool createdNew;
            _mutex = new Mutex(true, MutexName, out createdNew);
            if (!createdNew)
            {
                MessageBox.Show("xPuTTY 已在运行。\r\n请切换到已打开的窗口。", "xPuTTY",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => ReportError(e.Exception, "界面线程异常");
            AppDomain.CurrentDomain.UnhandledException +=
                (s, e) => ReportError(e.ExceptionObject as Exception, "未处理异常");

            try
            {
                // Load persisted state and regenerate the terminal theme INI
                // that patched putty.exe reads on startup.
                AppContext.I.Load();
                AppContext.I.BuildThemeIni();

                // Panels read Theme.Scale from their constructors, so it must
                // be right before MainForm creates them.  Graphics.FromHwnd(0)
                // can report the system DPI (96) in a per-monitor-v2 process,
                // so ask the user32 API first.
                float startupScale = 0f;
                try
                {
                    uint dpi = GetDpiForSystem();
                    if (dpi >= 48) startupScale = dpi / 96f;
                }
                catch { }
                if (startupScale < 0.5f)
                {
                    try
                    {
                        using (var g = Graphics.FromHwnd(IntPtr.Zero))
                            startupScale = g.DpiX / 96f;
                    }
                    catch { }
                }
                if (startupScale >= 0.5f) Theme.Scale = startupScale;
                UiFonts.Reset();

                var form = new MainForm();
                AppContext.I.Main = form;
                form.StartupArguments = args;
                Application.Run(form);
            }
            catch (Exception ex)
            {
                ReportError(ex, "启动失败");
            }
            finally
            {
                try { _mutex.ReleaseMutex(); }
                catch { /* not owner */ }
                _mutex.Dispose();
            }
        }

        private static void ReportError(Exception ex, string title)
        {
            if (ex == null) return;
            try
            {
                // Keep a full stack trace for diagnosis; the dialog only has
                // room for the message.
                string dir = AppContext.I.DataDir;
                if (!string.IsNullOrEmpty(dir))
                {
                    System.IO.Directory.CreateDirectory(dir);
                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(dir, "error.log"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + title + "]\r\n" +
                        ex + "\r\n\r\n",
                        new System.Text.UTF8Encoding(false));
                }
            }
            catch { }
            try
            {
                MessageBox.Show("xPuTTY 遇到错误：\r\n\r\n" + ex.Message + "\r\n\r\n" +
                                ex.GetType().Name,
                    "xPuTTY", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { /* nothing else to do */ }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetDpiForSystem();
    }

    /// <summary>
    /// Font factory for the shell.  Every size is a design-pixel value from
    /// design/DESIGN.md; Theme.UIFont/MonoFont multiply it by the current DPI
    /// scale and use GraphicsUnit.Pixel (never points, which would double up
    /// on high-DPI displays).
    ///
    /// IMPORTANT: this deliberately returns a NEW Font instance on every call
    /// (no shared cache).  Call sites use "using (var f = UiFonts.UI(...))",
    /// so a shared cached instance would be disposed by the first paint and the
    /// next paint would throw ArgumentException from Font.GetHeight.
    /// </summary>
    internal static class UiFonts
    {
        public static Font UI(float designPx, FontStyle style)
        {
            try
            {
                return Theme.UIFont(designPx, style);
            }
            catch
            {
                return new Font(FontFamily.GenericSansSerif,
                    Math.Max(1f, Theme.SF(designPx)), style, GraphicsUnit.Pixel);
            }
        }

        public static Font Mono(float designPx, FontStyle style)
        {
            try
            {
                return Theme.MonoFont(designPx, style);
            }
            catch
            {
                return new Font(FontFamily.GenericMonospace,
                    Math.Max(1f, Theme.SF(designPx)), style, GraphicsUnit.Pixel);
            }
        }

        /// <summary>Kept for call sites that want to react to a DPI change.</summary>
        public static void Reset()
        {
            // Nothing to reset: fonts are not cached any more.
        }
    }

    /// <summary>
    /// Lightweight opt-in tracing for development: set XPUTTY_DEBUG to a file
    /// path (that variable is also forwarded to the putty.exe children).
    /// Production runs never touch the disk here.
    /// </summary>
    internal static class Diag
    {
        private static readonly object Sync = new object();
        private static string _path;
        private static bool _checked;

        public static void Log(string message)
        {
            try
            {
                if (!_checked)
                {
                    _checked = true;
                    _path = Environment.GetEnvironmentVariable("XPUTTY_DEBUG");
                    if (!string.IsNullOrEmpty(_path))
                    {
                        string dir = System.IO.Path.GetDirectoryName(_path);
                        if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                    }
                }
                if (string.IsNullOrEmpty(_path)) return;
                lock (Sync)
                {
                    System.IO.File.AppendAllText(_path,
                        DateTime.Now.ToString("HH:mm:ss.fff") + " [shell] " + message + "\r\n",
                        new System.Text.UTF8Encoding(false));
                }
            }
            catch { }
        }
    }
}
