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

        public TrayApp()
        {
            if (!(SynchronizationContext.Current is WindowsFormsSynchronizationContext))
                SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            ui = SynchronizationContext.Current;

            int px = SystemInformation.SmallIconSize.Width; // tamaño real de la bandeja (proceso con DPI)
            iconOk = ToIcon(TrayIcons.Render(px, false));
            iconBad = ToIcon(TrayIcons.Render(px, true));

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

        static Icon ToIcon(Bitmap bmp)
        {
            try { return Icon.FromHandle(bmp.GetHicon()); }
            catch { return SystemIcons.Application; }
        }
    }

    /// <summary>
    /// Dibuja el icono de la bandeja con el mismo diseño que el del .exe (tools/make-icon.ps1):
    /// cuadrado redondeado con degradado azul-violeta y el trazo de pulso en blanco.
    /// Se dibuja en vectorial al tamaño real de la bandeja (nítido con cualquier escala de Windows)
    /// en lugar de reducir el .ico, cuyas imágenes PNG se ven mal a 16-24 px en .NET Framework.
    /// </summary>
    public static class TrayIcons
    {
        static readonly Color From = Color.FromArgb(255, 109, 140, 255);
        static readonly Color To   = Color.FromArgb(255, 155, 123, 255);
        static readonly Color Red  = Color.FromArgb(255, 255, 77, 94);

        /// <summary>alert = true añade un punto rojo pequeño abajo a la derecha (hay IPs caídas).</summary>
        public static Bitmap Render(int size, bool alert)
        {
            const int k = 4; // se dibuja a 4x y se reduce con filtro de alta calidad: bordes suaves
            int big = size * k;
            using (var hi = new Bitmap(big, big))
            {
                using (var g = Graphics.FromImage(hi))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    float s = big;
                    float m = s * 0.03f;                 // margen mínimo para que no toque el borde
                    var box = new RectangleF(m, m, s - 2 * m, s - 2 * m);
                    using (var path = Rounded(box, box.Width * 0.24f))
                    using (var brush = new LinearGradientBrush(box, From, To, 45f))
                        g.FillPath(brush, path);

                    // trazo de pulso (mismas coordenadas en rejilla de 24 que el icono del .exe)
                    Func<float, float, PointF> P = (x, y) => new PointF(m + (x / 24f) * box.Width, m + (y / 24f) * box.Height);
                    var pts = new[] { P(3.5f, 12.5f), P(7f, 12.5f), P(9.5f, 6f), P(13f, 18.5f), P(15.5f, 12.5f), P(20.5f, 12.5f) };
                    float w = Math.Max(s * 0.095f, 1.6f * k); // al menos ~1.6 px reales a 16 px
                    using (var pen = new Pen(Color.White, w) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawLines(pen, pts);

                    if (alert)
                    {
                        float d = s * 0.38f;                       // punto rojo pequeño
                        var dot = new RectangleF(s - d, s - d, d, d);
                        float ring = s * 0.045f;                   // aro fino que lo separa del logo
                        using (var b = new SolidBrush(Color.FromArgb(255, 22, 26, 36)))
                            g.FillEllipse(b, RectangleF.Inflate(dot, ring, ring));
                        using (var b = new SolidBrush(Red)) g.FillEllipse(b, dot);
                    }
                }
                var bmp = new Bitmap(size, size);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.DrawImage(hi, new Rectangle(0, 0, size, size));
                }
                return bmp;
            }
        }

        static GraphicsPath Rounded(RectangleF r, float radius)
        {
            float d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
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
