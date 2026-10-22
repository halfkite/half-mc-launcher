using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace HalfMcLauncher
{
    /// <summary>关于页所需的运行环境信息。</summary>
    internal static class About
    {
        public static JObject Info()
        {
            var webview = string.Empty;
            try { webview = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString() ?? string.Empty; }
            catch { }
            var instances = 0;
            try { instances = Program.MainWindow?.BuildSnapshot()["instances"]?.Count() ?? 0; }
            catch { }
            return new JObject
            {
                ["version"] = Program.Version,
                ["os"] = Environment.OSVersion.ToString(),
                ["x64"] = Environment.Is64BitOperatingSystem,
                ["clr"] = Environment.Version.ToString(),
                ["webview2"] = webview,
                ["baseDir"] = AppPaths.BaseDir,
                ["dataDir"] = AppPaths.DataDir,
                ["exe"] = System.Windows.Forms.Application.ExecutablePath,
                ["memoryMb"] = SystemMemory.TotalMegabytes(),
                ["instances"] = instances,
                ["notices"] = File.Exists(Path.Combine(AppPaths.BaseDir, "THIRD-PARTY-NOTICES.txt"))
            };
        }
    }
}
