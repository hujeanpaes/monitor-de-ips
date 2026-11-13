using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace MonitorIPs
{
    /// <summary>
    /// Cuadro "Guardar como" de Windows para los exportes y el informe PDF.
    /// Se abre en un hilo STA propio, centrado sobre la ventana del monitor y traído al frente
    /// (aunque el foco lo tenga Edge, que es otro proceso). Sólo puede haber uno abierto a la vez.
    /// </summary>
    public static class SaveDialog
    {
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }

        static readonly object gate = new object();
        static Form host;   // ventana invisible dueña del cuadro abierto (null si no hay ninguno)

        /// <summary>Ruta elegida, o null si se cancela. Lanza una excepción si ya hay un cuadro abierto (y lo trae al frente).</summary>
        public static string Ask(string fileName, string filter, string defaultExt, string initialDir)
        {
            lock (gate)
            {
                if (host != null)
                {
                    var h = host;
                    try { h.BeginInvoke((Action)(() => ForceForeground(h.Handle))); } catch { }
                    throw new Exception("Ya hay una ventana «Guardar como» abierta.");
                }
            }
            string result = null;
            Exception error = null;
            var t = new Thread(() =>
            {
                try
                {
                    var center = MonitorCenter();
                    using (var f = new Form
                    {
                        ShowInTaskbar = false, FormBorderStyle = FormBorderStyle.None, Opacity = 0, TopMost = true,
                        StartPosition = FormStartPosition.Manual, Size = new Size(1, 1), Location = center, Text = "Monitor de IPs",
                    })
                    using (var d = new SaveFileDialog
                    {
                        Title = "Guardar como", FileName = fileName, Filter = filter, DefaultExt = defaultExt, AddExtension = true,
                        OverwritePrompt = true, RestoreDirectory = true, InitialDirectory = initialDir,
                    })
                    {
                        f.Shown += (s, e) =>
                        {
                            ForceForeground(f.Handle);
                            if (d.ShowDialog(f) == DialogResult.OK) result = d.FileName;
                            f.Close();
                        };
                        lock (gate) host = f;
                        f.ShowDialog();
                    }
                }
                catch (Exception ex) { error = ex; }
                finally { lock (gate) host = null; }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.IsBackground = true;
            t.Start();
            t.Join();
            if (error != null) throw new Exception("No se pudo abrir «Guardar como»: " + error.Message);
            return result;
        }

        /// <summary>Centro de la ventana del monitor (o de la pantalla principal si no se encuentra).</summary>
        static Point MonitorCenter()
        {
            RECT r;
            IntPtr w = WindowFocus.Find();
            if (w != IntPtr.Zero && GetWindowRect(w, out r) && r.Right > r.Left)
                return new Point((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 3);
            var a = Screen.PrimaryScreen.WorkingArea;
            return new Point(a.Left + a.Width / 2, a.Top + a.Height / 3);
        }

        /// <summary>Trae una ventana propia al frente aunque el foco lo tenga otro proceso (Edge).</summary>
        static void ForceForeground(IntPtr h)
        {
            uint fg = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero), me = GetCurrentThreadId();
            bool attached = fg != 0 && fg != me && AttachThreadInput(me, fg, true);
            BringWindowToTop(h);
            SetForegroundWindow(h);
            if (attached) AttachThreadInput(me, fg, false);
        }
    }
}
