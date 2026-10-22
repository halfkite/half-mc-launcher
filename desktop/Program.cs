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
            if (args != null && args.Any(item => string.Equals(item, "--check-versions", StringComparison.OrdinalIgnoreCase)))
                return CheckVersions();

            Bootstrap.EnableDpiAwareness();
            Bootstrap.ExtractNativeLoader();
            Bootstrap.ExtractWebAssets();

            // 任何未处理异常都写日志并提示，而不是让窗口直接消失。
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (sender, e) => Report(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (sender, e) => Report(e.ExceptionObject as Exception);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            MainWindow = new MainForm();
            Application.Run(MainWindow);
            return 0;
        }

        private static void Report(Exception error)
        {
            if (error == null) return;
            try
            {
                Directory.CreateDirectory(AppPaths.LogDir);
                File.AppendAllText(Path.Combine(AppPaths.LogDir, "crash.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + error + Environment.NewLine);
            }
            catch { }
            try
            {
                MessageBox.Show("发生错误：" + error.Message + "\n\n详细信息已写入 data/logs/crash.log", "Half Mc Launcher",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }

        /// <summary>下载页“游戏源”自检：取 Mojang 版本清单并记录数量。</summary>
        private static int CheckVersions()
        {
            try
            {
                var list = (Newtonsoft.Json.Linq.JArray)HostApi.HandleAsync("versions", null).GetAwaiter().GetResult();
                File.WriteAllText(Path.Combine(AppPaths.DataDir, "versions-check.txt"),
                    "versions=" + list.Count + Environment.NewLine + "latest=" + list.FirstOrDefault()?["id"]);
                return list.Count > 0 ? 0 : 1;
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(AppPaths.DataDir, "versions-check.txt"), "error=" + error);
                return 1;
            }
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
