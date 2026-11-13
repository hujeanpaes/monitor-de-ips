using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Media;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MonitorIPs
{
    /// <summary>
    /// Icono en la bandeja del sistema y bucle principal de la aplicación (hilo STA de WinForms).
    /// Mantiene el monitor vivo con la ventana cerrada, muestra el estado y avisa de caídas.
    /// </summary>
    public class TrayApp : ApplicationContext
    {
        readonly NotifyIcon icon;
        readonly Icon iconOk, iconBad;
        readonly SynchronizationContext ui;
        readonly System.Windows.Forms.Timer timer;
        readonly ToolStripMenuItem miSound, miAutostart;
        bool showingBad;
        int ticks;

        public TrayApp(byte[] icoBytes)
        {
            if (!(SynchronizationContext.Current is WindowsFormsSynchronizationContext))
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            ui = SynchronizationContext.Current;

            iconOk = LoadIcon(icoBytes);
            iconBad = WithRedDot(iconOk);

            var menu = new ContextMenuStrip();
            var miOpen = new ToolStripMenuItem("Abrir monitor", null, (s, e) => Program.OpenOrFocusWindow());
            miOpen.Font = new Font(miOpen.Font, FontStyle.Bold);
            miSound = new ToolStripMenuItem("Sonido de alertas", null, (s, e) => Program.ToggleSound());
            miAutostart = new ToolStripMenuItem("Iniciar con Windows", null, (s, e) => Autostart.Set(!Autostart.IsEnabled()));
            menu.Items.AddRange(new ToolStripItem[] {
                miOpen,
                new ToolStripSeparator(),
                new ToolStripMenuItem("Pausar todas", null, (s, e) => Program.PauseAll(true)),
                new ToolStripMenuItem("Reanudar todas", null, (s, e) => Program.PauseAll(false)),
                new ToolStripSeparator(),
                miSound,
                miAutostart,
                new ToolStripSeparator(),
                new ToolStripMenuItem("Salir del monitor", null, (s, e) => ExitApp()),
            });
            menu.Opening += (s, e) => { miSound.Checked = Program.SoundEnabled; miAutostart.Checked = Autostart.IsEnabled(); };

            icon = new NotifyIcon { Icon = iconOk, Text = "Monitor de IPs", ContextMenuStrip = menu, Visible = true };
            icon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) Program.OpenOrFocusWindow(); };

            timer = new System.Windows.Forms.Timer { Interval = 1000 };
            timer.Tick += OnTick;
            timer.Start();
            UpdateStatus();
        }

        /// <summary>Ejecuta una acción en el hilo de la bandeja (desde HTTP o el motor).</summary>
        public void Post(Action a) { ui.Post(_ => { try { a(); } catch { } }, null); }

        public void ShowAlert(string title, string text, bool isDown)
        {
            icon.ShowBalloonTip(isDown ? 10000 : 6000, title, text, isDown ? ToolTipIcon.Error : ToolTipIcon.Info);
            if (Program.SoundEnabled) (isDown ? SystemSounds.Hand : SystemSounds.Asterisk).Play();
        }

        public void ShowHint(string title, string text) { icon.ShowBalloonTip(8000, title, text, ToolTipIcon.Info); }

        public void ExitApp()
        {
            timer.Stop();
            icon.Visible = false;
            icon.Dispose();
            ExitThread();
        }

        void OnTick(object sender, EventArgs e)
        {
            if (Program.Tick()) { ExitApp(); return; }
            if (++ticks % 2 == 0) UpdateStatus();
        }

        void UpdateStatus()
        {
            int[] c = Program.CountStates(); // en línea, caídas, pausadas, total
            string text = c[3] == 0 ? "Monitor de IPs — sin IPs"
                : "Monitor de IPs — " + c[0] + " en línea" + (c[1] > 0 ? " · " + c[1] + " caídas" : "") + (c[2] > 0 ? " · " + c[2] + " pausadas" : "");
            icon.Text = text.Length > 63 ? text.Substring(0, 63) : text; // límite de Windows
            bool bad = c[1] > 0;
            if (bad != showingBad) { icon.Icon = bad ? iconBad : iconOk; showingBad = bad; }
        }

        static Icon LoadIcon(byte[] ico)
        {
            try { if (ico != null) return new Icon(new MemoryStream(ico), SystemInformation.SmallIconSize); }
            catch { }
            return SystemIcons.Application;
        }

        /// <summary>Variante del icono con un punto rojo (hay IPs caídas).</summary>
        static Icon WithRedDot(Icon baseIcon)
        {
            try
            {
                var size = SystemInformation.SmallIconSize;
                using (var bmp = new Bitmap(size.Width, size.Height))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.DrawIcon(baseIcon, new Rectangle(Point.Empty, size));
                        float d = size.Width * 0.5f;
                        var r = new RectangleF(size.Width - d, size.Height - d, d - 0.5f, d - 0.5f);
                        g.FillEllipse(Brushes.White, RectangleF.Inflate(r, 1, 1));
                        using (var red = new SolidBrush(Color.FromArgb(255, 77, 94))) g.FillEllipse(red, r);
                    }
                    return Icon.FromHandle(bmp.GetHicon());
                }
            }
            catch { return baseIcon; }
        }
    }

    /// <summary>Busca la ventana del monitor (Edge/Chrome en modo app) y la trae al frente.</summary>
    public static class WindowFocus
    {
        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int max);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        const int SW_RESTORE = 9;

        public static bool TryFocus()
        {
            IntPtr found = IntPtr.Zero;
            try
            {
                EnumWindows((h, l) =>
                {
                    if (!IsWindowVisible(h)) return true;
                    var sb = new StringBuilder(256);
                    GetWindowText(h, sb, sb.Capacity);
                    if (sb.ToString().IndexOf("Monitor de IPs", StringComparison.Ordinal) < 0) return true;
                    uint pid; GetWindowThreadProcessId(h, out pid);
                    string proc;
                    try { proc = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant(); } catch { return true; }
                    if (proc != "msedge" && proc != "chrome" && proc != "brave") return true;
                    found = h;
                    return false;
                }, IntPtr.Zero);
            }
            catch { }
            if (found == IntPtr.Zero) return false;
            if (IsIconic(found)) ShowWindow(found, SW_RESTORE);
            SetForegroundWindow(found);
            return true;
        }
    }

    /// <summary>Inicio con Windows: valor en HKCU\...\Run que arranca el monitor directamente en la bandeja.</summary>
    public static class Autostart
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "MonitorIPs";

        static string Command { get { return "\"" + Application.ExecutablePath + "\" --tray"; } }

        static string Current()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k == null ? null : k.GetValue(ValueName) as string; }
            catch { return null; }
        }

        public static bool IsEnabled() { return !string.IsNullOrEmpty(Current()); }

        public static void Set(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(ValueName, Command);
                    else k.DeleteValue(ValueName, false);
                }
            }
            catch { }
        }

        /// <summary>Si el inicio automático apunta a un .exe que ya no existe (portable movido), lo corrige.</summary>
        public static void RepairPath()
        {
            string cur = Current();
            if (string.IsNullOrEmpty(cur) || cur == Command) return;
            string path = cur.StartsWith("\"") ? cur.Substring(1, Math.Max(0, cur.IndexOf('"', 1) - 1)) : cur.Split(' ')[0];
            if (!File.Exists(path)) Set(true);
        }
    }
}
