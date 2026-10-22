using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace HalfMcLauncher
{
    /// <summary>
    /// 诊断包：把启动日志、脱敏配置、实例清单、Java 列表和系统信息打成 ZIP，
    /// 方便排查问题时一次性提供上下文，同时避免泄露账户凭据。
    /// </summary>
    internal static class DiagnosticsExport
    {
        public static JObject Export(string path)
        {
            var store = Program.MainWindow?.Store;
            var state = store?.State ?? new JObject();
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                void Write(string name, string content)
                {
                    var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                    using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                        writer.Write(content);
                }

                Write("readme.txt",
                    "Half Mc Launcher 诊断包" + Environment.NewLine +
                    "版本：" + Program.Version + Environment.NewLine +
                    "生成时间：" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + Environment.NewLine +
                    "账户名称、UUID、皮肤、客户端 ID 已脱敏；不包含登录凭据。" + Environment.NewLine);

                var system = new StringBuilder();
                system.AppendLine("version=" + Program.Version);
                system.AppendLine("os=" + Environment.OSVersion);
                system.AppendLine("64bitOS=" + Environment.Is64BitOperatingSystem);
                system.AppendLine("64bitProcess=" + Environment.Is64BitProcess);
                system.AppendLine("clr=" + Environment.Version);
                system.AppendLine("baseDir=" + AppPaths.BaseDir);
                system.AppendLine("dataDir=" + AppPaths.DataDir);
                system.AppendLine("memoryMb=" + SystemMemory.TotalMegabytes());
                system.AppendLine("instances=" + (Program.MainWindow?.BuildSnapshot()["instances"]?.Count() ?? 0));
                Write("system.txt", system.ToString());

                var log = Path.Combine(AppPaths.LogDir, "startup.log");
                if (File.Exists(log))
                {
                    using (var input = File.OpenRead(log))
                    {
                        var start = Math.Max(0, input.Length - 1536 * 1024);
                        input.Seek(start, SeekOrigin.Begin);
                        var buffer = new byte[input.Length - start];
                        input.Read(buffer, 0, buffer.Length);
                        var entry = archive.CreateEntry("startup.log", CompressionLevel.Optimal);
                        using (var output = entry.Open()) output.Write(buffer, 0, buffer.Length);
                    }
                }

                var sanitized = (JObject)state.DeepClone();
                if (sanitized["accounts"] is JArray accounts)
                {
                    var index = 0;
                    foreach (var account in accounts.OfType<JObject>())
                    {
                        index++;
                        account["name"] = "account-" + index;
                        account["uuid"] = "***";
                        account["skin"] = string.Empty;
                        account["cape"] = string.Empty;
                    }
                }
                sanitized["selectedAccount"] = string.Empty;
                if (sanitized["settings"] is JObject settings) settings["clientId"] = "***";
                Write("config.sanitized.json", sanitized.ToString(Newtonsoft.Json.Formatting.Indented));

                var snapshot = Program.MainWindow?.BuildSnapshot();
                if (snapshot?["instances"] is JArray instances)
                {
                    var list = new JArray(instances.OfType<JObject>().Select(item => new JObject
                    {
                        ["id"] = item["id"],
                        ["name"] = item["name"],
                        ["root"] = item["root"],
                        ["gameDir"] = item["gameDir"],
                        ["version"] = item["version"],
                        ["loader"] = item["loader"],
                        ["isolated"] = item["isolated"],
                        ["protected"] = item["protected"]
                    }));
                    Write("instances.json", list.ToString(Newtonsoft.Json.Formatting.Indented));
                }

                try
                {
                    var roots = (state["settings"]?["javaRoots"] as JArray)?.Values<string>() ?? Enumerable.Empty<string>();
                    var java = new JArray(JavaDetector.Discover(roots, null).Select(item => new JObject
                    {
                        ["path"] = item["path"],
                        ["version"] = item["version"],
                        ["major"] = item["major"],
                        ["arch"] = item["arch"]
                    }));
                    Write("java.json", java.ToString(Newtonsoft.Json.Formatting.Indented));
                }
                catch { }

                var diagnose = Path.Combine(AppPaths.DataDir, "diagnose.txt");
                if (File.Exists(diagnose))
                {
                    var entry = archive.CreateEntry("diagnose.txt", CompressionLevel.Optimal);
                    using (var output = entry.Open())
                    using (var input = File.OpenRead(diagnose))
                        input.CopyTo(output);
                }
            }

            return new JObject
            {
                ["path"] = path,
                ["bytes"] = new FileInfo(path).Length,
                ["entries"] = 6
            };
        }
    }
}
