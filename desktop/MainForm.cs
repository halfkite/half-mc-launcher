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
        [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("gdi32.dll")] private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int width, int height);
        [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr handle);

        private readonly ConfigStore store;
        private readonly LaunchService launch;
        private ScanResult scan;
        private WebView2 web;
        private bool closing;
        private bool applyingCorners;
        private bool lastMaximized;

        public MainForm()
        {
            store = ConfigStore.Load();
            scan = InstanceScanner.Scan(store.State);
            launch = new LaunchService(store, (type, data) => SendEvent(type, data), () => BuildSnapshot());

            Text = "Half Mc Launcher";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            FormBorderStyle = FormBorderStyle.None;
            var dark = string.Equals(store.Settings.Value<string>("theme"), "dark", StringComparison.OrdinalIgnoreCase);
            BackColor = dark ? Color.FromArgb(14, 17, 16) : Color.FromArgb(246, 248, 246);
            MinimumSize = new Size(1050, 680);
            StartPosition = FormStartPosition.CenterScreen;
            DoubleBuffered = true;

            Shown += async (sender, args) => { Diagnostics.Write("form shown"); await InitializeBrowserAsync(); };
            ApplyRememberedBounds();
            FormClosing += OnClosing;
            launch.Log("Half Mc Launcher " + Program.Version + " 已就绪");
            Diagnostics.Write("form constructed");
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyCorners();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ApplyCorners();
            var maximized = WindowState == FormWindowState.Maximized;
            if (maximized == lastMaximized) return;
            lastMaximized = maximized;
            SendEvent("windowState", new JObject { ["maximized"] = maximized });
        }

        /// <summary>Windows 11 用 DWM 圆角；Windows 10 退化为窗口区域裁剪。</summary>
        private void ApplyCorners()
        {
            if (!IsHandleCreated || applyingCorners) return;
            applyingCorners = true;
            try
            {
                var maximized = WindowState == FormWindowState.Maximized;
                var radius = (int)Math.Round(10 * (DeviceDpi / 96.0));
                // Windows 11 支持 DWM 圆角；Windows 10 上这个属性不存在，改用区域裁剪。
                var preference = maximized ? 1 : 2;
                if (DwmSetWindowAttribute(Handle, 33, ref preference, sizeof(int)) == 0)
                {
                    var region = Region;
                    Region = null;
                    region?.Dispose();
                    return;
                }
                if (maximized)
                {
                    var expanded = Region;
                    Region = null;
                    expanded?.Dispose();
                    return;
                }
                var handle = CreateRoundRectRgn(0, 0, Width + 1, Height + 1, radius * 2, radius * 2);
                var previous = Region;
                Region = Region.FromHrgn(handle);
                DeleteObject(handle);
                previous?.Dispose();
            }
            catch { }
            finally
            {
                applyingCorners = false;
            }
        }

        internal ConfigStore Store { get { return store; } }
        internal LaunchService Launch { get { return launch; } }

        internal JObject BuildSnapshot()
        {
            var instances = new JArray();
            foreach (var instance in scan.Instances)
            {
                var entry = (JObject)instance.DeepClone();
                entry["running"] = launch.IsRunning(entry.Value<string>("id"));
                instances.Add(entry);
            }
            return new JObject
            {
                ["state"] = store.State,
                ["instances"] = instances,
                ["tasks"] = launch.TasksJson(),
                ["logs"] = launch.LogsJson(),
                ["dataPath"] = AppPaths.DataDir,
                ["appVersion"] = Program.Version,
                ["memoryMb"] = SystemMemory.TotalMegabytes(),
                ["scanWarnings"] = new JArray(scan.Warnings),
                ["maximized"] = WindowState == FormWindowState.Maximized,
                ["startupInstance"] = Program.StartupInstance ?? string.Empty,
                ["authConfigured"] = true,
                ["authClientId"] = MicrosoftAuth.ClientId(),
                ["authSource"] = MicrosoftAuth.ClientIdSource()
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

        internal void NavigateAuth(string url)
        {
            try { if (web?.CoreWebView2 != null) web.CoreWebView2.Navigate(url); } catch { }
        }

        internal void ReturnToApp()
        {
            try { if (web?.CoreWebView2 != null) web.CoreWebView2.Navigate("https://app.local/index.html"); } catch { }
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            Diagnostics.Write("closing requested, closing=" + closing);
            if (closing) return;
            if (MicrosoftAuth.IsPending)
            {
                // 应用内登录时页面已经离开启动器，无法完成 closeRequested 握手，直接关闭。
                closing = true;
                SaveWindowBounds();
                Close();
                return;
            }
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

        public static int AvailableMegabytes()
        {
            var status = new MemoryStatus { Length = (uint)Marshal.SizeOf(typeof(MemoryStatus)) };
            if (!GlobalMemoryStatusEx(status)) return 0;
            return (int)(status.AvailablePhysical / 1024 / 1024);
        }
    }
}
