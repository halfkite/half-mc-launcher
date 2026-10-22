using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace HalfMcLauncher
{
    /// <summary>
    /// 单文件发行所需的启动步骤：把内嵌的前端资源与 WebView2 原生加载器解压到 data/，
    /// 并在创建任何窗口前设置 DPI 感知（原先靠 App.config，单文件后不再带 .config）。
    /// </summary>
    internal static class Bootstrap
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string path);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string path);

        private static readonly IntPtr PerMonitorAwareV2 = new IntPtr(-4);

        public static void EnableDpiAwareness()
        {
            try { if (SetProcessDpiAwarenessContext(PerMonitorAwareV2)) return; }
            catch { }
            try { SetProcessDPIAware(); } catch { }
        }

        public static void ExtractNativeLoader()
        {
            var directory = Path.Combine(AppPaths.DataDir, "native");
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, "WebView2Loader.dll");
            Extract("native/WebView2Loader.dll", target);
            if (File.Exists(target))
            {
                SetDllDirectory(directory);
                LoadLibrary(target);
            }
        }

        public static void ExtractWebAssets()
        {
            var root = AppPaths.WebRoot;
            Directory.CreateDirectory(root);
            var assembly = Assembly.GetExecutingAssembly();
            foreach (var name in assembly.GetManifestResourceNames())
            {
                if (!name.StartsWith("web/", StringComparison.Ordinal)) continue;
                var relative = name.Substring(4).Replace('\\', '/');
                if (relative.Length == 0) continue;
                var target = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                Extract(name, target);
            }
        }

        private static void Extract(string resourceName, string target)
        {
            var assembly = Assembly.GetExecutingAssembly();
            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null) return;
                var directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                if (File.Exists(target) && new FileInfo(target).Length == stream.Length) return;
                using (var file = File.Create(target)) stream.CopyTo(file);
            }
        }
    }
}
