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
        private static extern bool SetProcessDPIAware();

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectory(string path);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibrary(string path);

        public static void EnableDpiAwareness()
        {
            // .NET Framework 4.8 的 WinForms 在没有 App.config 时按系统 DPI 工作。
            // 单文件版不带 .config，因此显式声明系统级 DPI，避免按显示器 DPI 与
            // 原生文件夹对话框混用时崩溃或错位。
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
                // 前端构建产物的文件名带哈希，index.html 的大小却几乎不变；按大小跳过
                // 会让新版本一直加载旧页面，所以这里每次都覆盖写入。
                using (var file = File.Create(target)) stream.CopyTo(file);
            }
        }
    }
}
