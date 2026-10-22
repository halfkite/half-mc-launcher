using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace HalfMcLauncher
{
    internal static class Program
    {
        internal static MainForm MainWindow { get; private set; }

        [STAThread]
        private static int Main(string[] args)
        {
            AppPaths.Initialize();

            if (args != null && args.Any(item => string.Equals(item, "--selftest", StringComparison.OrdinalIgnoreCase)))
                return SelfTest();

            Bootstrap.EnableDpiAwareness();
            Bootstrap.ExtractNativeLoader();
            Bootstrap.ExtractWebAssets();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            MainWindow = new MainForm();
            Application.Run(MainWindow);
            return 0;
        }

        /// <summary>
        /// 无界面自检：验证单文件里的内嵌程序集能被解析、前端资源与原生加载器能解压。
        /// 用法：Half Mc Launcher.exe --selftest（结果写入 data/selftest.txt）。
        /// </summary>
        private static int SelfTest()
        {
            var report = new List<string>();
            try
            {
                Bootstrap.ExtractNativeLoader();
                Bootstrap.ExtractWebAssets();
                report.Add("native=" + File.Exists(Path.Combine(AppPaths.DataDir, "native", "WebView2Loader.dll")));
                report.Add("web=" + File.Exists(Path.Combine(AppPaths.WebRoot, "index.html")));
                report.Add("newtonsoft=" + (Type.GetType("Newtonsoft.Json.JsonConvert, Newtonsoft.Json") != null));
                report.Add("cmllib=" + (Type.GetType("CmlLib.Core.MinecraftLauncher, CmlLib.Core") != null));
                report.Add("webview2=" + (Type.GetType("Microsoft.Web.WebView2.Core.CoreWebView2Environment, Microsoft.Web.WebView2.Core") != null));
            }
            catch (Exception error)
            {
                report.Add("error=" + error);
            }
            File.WriteAllLines(Path.Combine(AppPaths.DataDir, "selftest.txt"), report);
            return report.All(line => !line.Contains("False") && !line.StartsWith("error=")) ? 0 : 1;
        }
    }
}
