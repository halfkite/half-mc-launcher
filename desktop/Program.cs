using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using ICSharpCode.SharpZipLib.Zip;
using Newtonsoft.Json.Linq;

namespace HalfMcLauncher
{
    internal static class Program
    {
        internal static MainForm MainWindow { get; private set; }

        /// <summary>发行标记，形如 0.1.0-r6，取自程序集 InformationalVersion。</summary>
        internal static string Version
        {
            get
            {
                var attribute = (System.Reflection.AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
                    System.Reflection.Assembly.GetExecutingAssembly(), typeof(System.Reflection.AssemblyInformationalVersionAttribute));
                var value = attribute?.InformationalVersion ?? "0.1.0";
                var plus = value.IndexOf('+');
                return plus > 0 ? value.Substring(0, plus) : value;
            }
        }

        [STAThread]
        private static int Main(string[] args)
        {
            Diagnostics.Write("start pid=" + System.Diagnostics.Process.GetCurrentProcess().Id + " args=" + string.Join(" ", args ?? Array.Empty<string>()));
            AppPaths.Initialize();
            Diagnostics.Write("paths ready data=" + AppPaths.DataDir);

            if (args != null && args.Any(item => string.Equals(item, "--selftest", StringComparison.OrdinalIgnoreCase)))
                return SelfTest();
            if (args != null && args.Any(item => string.Equals(item, "--check-versions", StringComparison.OrdinalIgnoreCase)))
                return CheckVersions();
            if (args != null && args.Any(item => string.Equals(item, "--check-downloads", StringComparison.OrdinalIgnoreCase)))
                return CheckDownloads();

            Bootstrap.EnableDpiAwareness();
            Bootstrap.ExtractNativeLoader();
            Bootstrap.ExtractWebAssets();
            Diagnostics.Write("bootstrap ready");

            // 任何未处理异常都写日志并提示，而不是让窗口直接消失。
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (sender, e) => Report(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (sender, e) => Report(e.ExceptionObject as Exception);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Diagnostics.Write("creating form");
            MainWindow = new MainForm();
            Diagnostics.Write("form created, running");
            Application.Run(MainWindow);
            Diagnostics.Write("application exited");
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

        /// <summary>下载与模组管线的无界面自检：Modrinth 搜索、Fabric 版本、JAR 信息与图标。</summary>
        private static int CheckDownloads()
        {
            var lines = new List<string>();
            var ok = true;
            try
            {
                var resources = new ResourceService(AppPaths.DataDir);
                var search = resources.SearchAsync(new JObject
                {
                    ["source"] = "modrinth",
                    ["kind"] = "mod",
                    ["query"] = "sodium",
                    ["offset"] = 0
                }, CancellationToken.None).GetAwaiter().GetResult();
                var total = search.Value<int?>("total") ?? 0;
                var first = search["projects"]?.FirstOrDefault();
                lines.Add("search=" + total + " first=" + first?["name"]);
                if (total <= 0) ok = false;

                var projectId = first?["id"]?.ToString();
                var gameRoot = Path.Combine(AppPaths.DataDir, "dl-test");
                Directory.CreateDirectory(Path.Combine(gameRoot, "mods"));
                var instance = new JObject
                {
                    ["id"] = "dl", ["root"] = gameRoot, ["gameDir"] = gameRoot,
                    ["name"] = "dl-test", ["minecraft"] = "1.21.1", ["loader"] = "Fabric"
                };
                var selection = new JObject { ["source"] = "modrinth", ["kind"] = "mod", ["projectId"] = projectId };
                var versions = resources.VersionsAsync(selection, instance, CancellationToken.None).GetAwaiter().GetResult();
                lines.Add("versions=" + versions.Count);
                if (versions.Count == 0) ok = false;
                else
                {
                    selection["versionId"] = versions[0]?["id"]?.ToString();
                    var plan = resources.PlanAsync(selection, instance, CancellationToken.None).GetAwaiter().GetResult();
                    lines.Add("plan=" + plan.Count);
                    var installed = resources.InstallAsync(selection, instance, _ => { }, CancellationToken.None).GetAwaiter().GetResult();
                    var files = Directory.GetFiles(Path.Combine(gameRoot, "mods")).Length;
                    lines.Add("installed=" + installed + " files=" + files);
                    if (installed <= 0 || files <= 0) ok = false;
                }
                Directory.Delete(gameRoot, true);
            }
            catch (Exception error) { ok = false; lines.Add("search-error=" + error.Message); }

            try
            {
                foreach (var kind in new[] { "fabric", "quilt", "forge", "neoforge", "optifine", "liteloader" })
                {
                    try
                    {
                        var target = kind == "liteloader" ? "1.12.2" : "1.21.1";
                        var list = Loaders.ListAsync(target, kind, CancellationToken.None).GetAwaiter().GetResult();
                        lines.Add(kind + "-loaders=" + list.Count);
                    }
                    catch (Exception error) { ok = false; lines.Add(kind + "-loaders-error=" + error.Message); }
                }
            }
            catch (Exception error) { ok = false; lines.Add("loader-error=" + error.Message); }

            try
            {
                var root = Path.Combine(AppPaths.DataDir, "modtest");
                var modsDir = Path.Combine(root, "mods");
                Directory.CreateDirectory(modsDir);
                var jar = Path.Combine(modsDir, "demo.jar");
                using (var file = File.Create(jar))
                using (var zip = new ZipOutputStream(file))
                {
                    void Add(string name, byte[] data)
                    {
                        var entry = new ZipEntry(name) { Size = data.Length };
                        zip.PutNextEntry(entry);
                        zip.Write(data, 0, data.Length);
                        zip.CloseEntry();
                    }
                    Add("fabric.mod.json", Encoding.UTF8.GetBytes("{\"id\":\"demo\",\"name\":\"Demo Mod\",\"version\":\"1.0\",\"icon\":\"icon.png\"}"));
                    Add("icon.png", new byte[] { 0x89, 0x50, 0x4E, 0x47 });
                }
                var instance = new JObject { ["id"] = "modtest", ["root"] = root, ["gameDir"] = root, ["name"] = "modtest" };
                var list = Mods.List(instance);
                var hasIcon = !string.IsNullOrEmpty(list.FirstOrDefault()?["icon"]?.ToString());
                lines.Add("mods=" + list.Count + " name=" + list.FirstOrDefault()?["name"] + " icon=" + hasIcon);
                if (list.Count != 1 || !hasIcon) ok = false;
                Directory.Delete(root, true);
            }
            catch (Exception error) { ok = false; lines.Add("mod-error=" + error.Message); }

            File.WriteAllLines(Path.Combine(AppPaths.DataDir, "download-check.txt"), lines);
            return ok ? 0 : 1;
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
                report.Add("version=" + Version);
                report.Add("native=" + File.Exists(Path.Combine(AppPaths.DataDir, "native", "WebView2Loader.dll")));
                report.Add("web=" + File.Exists(Path.Combine(AppPaths.WebRoot, "index.html")));
                report.Add("newtonsoft=" + (Type.GetType("Newtonsoft.Json.JsonConvert, Newtonsoft.Json") != null));
                report.Add("cmllib=" + (Type.GetType("CmlLib.Core.MinecraftLauncher, CmlLib.Core") != null));
                report.Add("forge=" + (Type.GetType("CmlLib.Core.Installer.Forge.ForgeInstaller, CmlLib.Core.Installer.Forge") != null));
                report.Add("sharpzip=" + (Type.GetType("ICSharpCode.SharpZipLib.Zip.ZipFile, ICSharpCode.SharpZipLib") != null));
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
