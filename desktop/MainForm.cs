using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json.Linq;

namespace HalfMcLauncher
{
    internal sealed partial class MainForm : Form
    {
        private const int WM_NCLBUTTONDOWN = 0xA1;
        private const int HTCAPTION = 0x2;
        private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14;
        private const int HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

        [DllImport("user32.dll")] private static extern bool ReleaseCapture();
        [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

        private readonly ConfigStore store;
        private ScanResult scan;
        private WebView2 web;
        private bool closing;

        public MainForm()
        {
            store = ConfigStore.Load();
            scan = InstanceScanner.Scan(store.State);

            Text = "Half Mc Launcher";
            FormBorderStyle = FormBorderStyle.None;
            BackColor = Color.FromArgb(14, 17, 16);
            MinimumSize = new Size(1050, 680);
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;

            Shown += async (sender, args) => await InitializeBrowserAsync();
            ApplyRememberedBounds();
            FormClosing += OnClosing;
        }

        internal ConfigStore Store { get { return store; } }

        internal JObject BuildSnapshot()
        {
            return new JObject
            {
                ["state"] = store.State,
                ["instances"] = new JArray(scan.Instances),
                ["tasks"] = new JArray(),
                ["logs"] = new JArray(),
                ["dataPath"] = AppPaths.DataDir,
                ["appVersion"] = "0.1.0",
                ["memoryMb"] = SystemMemory.TotalMegabytes(),
                ["scanWarnings"] = new JArray(scan.Warnings),
                ["authConfigured"] = false,
                ["authClientId"] = "",
                ["authSource"] = "none"
            };
        }

        internal ScanResult Rescan()
        {
            scan = InstanceScanner.Scan(store.State);
            return scan;
        }

        private void ApplyRememberedBounds()
        {
            var bounds = store.State["windowBounds"] as JObject;
            var width = bounds?.Value<int?>("width") ?? 1280;
            var height = bounds?.Value<int?>("height") ?? 820;
            var area = Screen.PrimaryScreen.WorkingArea;
            width = Math.Max(MinimumSize.Width, Math.Min(area.Width, width));
            height = Math.Max(MinimumSize.Height, Math.Min(area.Height, height));
            var x = bounds?.Value<int?>("x");
            var y = bounds?.Value<int?>("y");
            StartPosition = FormStartPosition.Manual;
            Location = new Point(
                x.HasValue ? Math.Max(area.X, Math.Min(x.Value, area.Right - width)) : area.X + (area.Width - width) / 2,
                y.HasValue ? Math.Max(area.Y, Math.Min(y.Value, area.Bottom - height)) : area.Y + (area.Height - height) / 2);
            Size = new Size(width, height);
            if (bounds?.Value<bool?>("maximized") == true) ToggleMaximize();
        }

        private void SaveWindowBounds()
        {
            if (store == null) return;
            var restore = WindowState == FormWindowState.Maximized ? RestoreBounds : Bounds;
            store.State["windowBounds"] = new JObject
            {
                ["width"] = restore.Width,
                ["height"] = restore.Height,
                ["x"] = restore.X,
                ["y"] = restore.Y,
                ["maximized"] = WindowState == FormWindowState.Maximized
            };
            try { store.Save(); } catch { }
        }

        internal void ToggleMaximize()
        {
            if (WindowState == FormWindowState.Maximized) { WindowState = FormWindowState.Normal; return; }
            MaximizedBounds = Screen.FromControl(this).WorkingArea;
            WindowState = FormWindowState.Maximized;
        }

        internal void StartDrag()
        {
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, HTCAPTION, 0);
        }

        internal void StartResize(string edge)
        {
            int code;
            switch (edge)
            {
                case "left": code = HTLEFT; break;
                case "right": code = HTRIGHT; break;
                case "top": code = HTTOP; break;
                case "bottom": code = HTBOTTOM; break;
                case "topleft": code = HTTOPLEFT; break;
                case "topright": code = HTTOPRIGHT; break;
                case "bottomleft": code = HTBOTTOMLEFT; break;
                case "bottomright": code = HTBOTTOMRIGHT; break;
                default: return;
            }
            if (WindowState == FormWindowState.Maximized) WindowState = FormWindowState.Normal;
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, code, 0);
        }

        internal void CloseReady()
        {
            closing = true;
            SaveWindowBounds();
            Close();
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (closing) return;
            e.Cancel = true;
            SendEvent("closeRequested", null);
        }
    }

    internal static class SystemMemory
    {
        [StructLayout(LayoutKind.Sequential)]
        private sealed class MemoryStatus
        {
            public uint Length;
            public uint MemoryLoad;
            public ulong TotalPhysical;
            public ulong AvailablePhysical;
            public ulong TotalPageFile;
            public ulong AvailablePageFile;
            public ulong TotalVirtual;
            public ulong AvailableVirtual;
            public ulong AvailableExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatus buffer);

        public static int TotalMegabytes()
        {
            var status = new MemoryStatus { Length = (uint)Marshal.SizeOf(typeof(MemoryStatus)) };
            if (!GlobalMemoryStatusEx(status)) return 0;
            return (int)(status.TotalPhysical / 1024 / 1024);
        }
    }
}
