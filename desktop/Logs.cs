using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace HalfMcLauncher
{
    /// <summary>
    /// 实例日志与崩溃报告：列出、读取并给出常见原因分析。
    /// </summary>
    internal static class Logs
    {
        private const int MaxRead = 2 * 1024 * 1024;

        private sealed class LogFile
        {
            public string Path;
            public string Name;
            public string Kind;
            public long Size;
            public DateTime Modified;
        }

        private static string GameDir(JObject instance) => InstanceScanner.canonical(instance.Value<string>("gameDir"));

        private static IEnumerable<LogFile> Files(JObject instance)
        {
            var gameDir = GameDir(instance);
            var logs = Path.Combine(gameDir, "logs");
            if (Directory.Exists(logs))
            {
                foreach (var file in Directory.EnumerateFiles(logs, "*.log").Concat(Directory.EnumerateFiles(logs, "*.txt")))
                {
                    var info = new FileInfo(file);
                    if (info.Length == 0) continue;
                    yield return new LogFile { Path = file, Name = info.Name, Kind = "log", Size = info.Length, Modified = info.LastWriteTime };
                }
            }
            var reports = Path.Combine(gameDir, "crash-reports");
            if (Directory.Exists(reports))
            {
                foreach (var file in Directory.EnumerateFiles(reports, "*.txt"))
                {
                    var info = new FileInfo(file);
                    yield return new LogFile { Path = file, Name = info.Name, Kind = "crash", Size = info.Length, Modified = info.LastWriteTime };
                }
            }
        }

        public static JArray ListFiles(JObject instance)
        {
            var list = new JArray();
            foreach (var file in Files(instance).OrderByDescending(item => item.Modified))
                list.Add(new JObject
                {
                    ["file"] = file.Name,
                    ["kind"] = file.Kind,
                    ["size"] = file.Size,
                    ["modified"] = file.Modified.ToString("o")
                });
            return list;
        }

        private static string Resolve(JObject instance, string file)
        {
            var match = Files(instance).FirstOrDefault(item => string.Equals(item.Name, file, StringComparison.OrdinalIgnoreCase));
            if (match == null) throw new InvalidOperationException("日志不存在：" + file);
            return match.Path;
        }

        private static string Read(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
            {
                if (stream.Length > MaxRead)
                {
                    stream.Seek(-MaxRead, SeekOrigin.End);
                    reader.DiscardBufferedData();
                }
                return reader.ReadToEnd();
            }
        }

        public static JObject ReadFile(JObject instance, string file)
        {
            var path = Resolve(instance, file);
            var text = Read(path);
            return new JObject
            {
                ["file"] = Path.GetFileName(path),
                ["text"] = text,
                ["truncated"] = new FileInfo(path).Length > MaxRead
            };
        }

        public static void Reveal(JObject instance, string file)
        {
            var path = Resolve(instance, file);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
        }

        private sealed class Rule
        {
            public string Level;
            public string Title;
            public string Detail;
            public string[] Needles;
        }

        private static readonly Rule[] Rules =
        {
            new Rule { Level = "error", Title = "内存不足", Detail = "游戏分配的堆内存不够，模组较多时尤其明显。到实例设置里调高内存，或减少同时加载的模组。", Needles = new[] { "java.lang.OutOfMemoryError", "There is insufficient memory for the Java Runtime", "OutOfMemoryError" } },
            new Rule { Level = "error", Title = "Java 版本不匹配", Detail = "当前 Java 版本无法运行这个游戏或模组。1.17+ 需要 Java 17，1.20.5+ 需要 Java 21；也可检查实例 Java 路径是否指向了旧版本。", Needles = new[] { "UnsupportedClassVersionError", "class file version", "requires Java ", "has been compiled by a more recent version" } },
            new Rule { Level = "error", Title = "缺少前置模组", Detail = "有模组缺少依赖或依赖版本不对。查看崩溃报告里的 Missing or unsupported mandatory dependencies，在下载页补齐前置。", Needles = new[] { "Missing or unsupported mandatory dependencies", "ModResolutionException", "requires any version", "depends on" } },
            new Rule { Level = "error", Title = "模组冲突", Detail = "多个模组改动了同一个类或方法。按崩溃报告里的模组名逐个禁用排查，优先检查最近新增的模组。", Needles = new[] { "NoSuchMethodError", "NoClassDefFoundError", "ClassCastException", "Mixin apply failed", "MixinApplyError", "InjectionError" } },
            new Rule { Level = "warn", Title = "客户端被 Watchdog 终止", Detail = "游戏主线程被看门狗判定卡死，常见于大量模组同时加载或某个模组阻塞。先减少同时加载的模组，或按最近新增模组二分排查。", Needles = new[] { "Client shutdown from post-main", "ServerWatchdog", "Watchdog" } },
            new Rule { Level = "error", Title = "显卡 / OpenGL 问题", Detail = "显卡驱动过旧或选择了错误的图形后端。更新显卡驱动；也可尝试在实例设置里改用软件渲染或关闭光影。", Needles = new[] { "GLFW error", "OpenGL error", "Pixel format", "Failed to create window", "org.lwjgl.opengl" } },
            new Rule { Level = "warn", Title = "网络下载失败", Detail = "启动时下载资源失败，多为网络或镜像问题。检查下载源设置后重试，必要时手动补全资源。", Needles = new[] { "Failed to download", "ConnectException", "UnknownHostException", "SocketTimeoutException", "Connection reset" } },
            new Rule { Level = "warn", Title = "资源或数据包错误", Detail = "某个资源包/数据包格式不兼容。禁用最近加入的资源包后重试。", Needles = new[] { "Failed to load resource pack", "pack.mcmeta", "DataPack" } },
            new Rule { Level = "warn", Title = "配置文件损坏", Detail = "某个模组或游戏的配置文件无法解析。删除崩溃报告里提到的配置文件，游戏会重新生成。", Needles = new[] { "JsonParseException", "MalformedJsonException", "Failed to parse" } }
        };

        public static JObject Analyze(JObject instance, string file)
        {
            var path = Resolve(instance, file);
            var text = Read(path);
            var findings = new JArray();
            foreach (var rule in Rules)
            {
                var lines = new List<string>();
                foreach (var line in text.Split('\n'))
                {
                    if (rule.Needles.Any(needle => line.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0))
                        lines.Add(line.Trim());
                    if (lines.Count >= 4) break;
                }
                if (lines.Count == 0) continue;
                findings.Add(new JObject
                {
                    ["level"] = rule.Level,
                    ["title"] = rule.Title,
                    ["detail"] = rule.Detail,
                    ["lines"] = new JArray(lines)
                });
            }

            // 提取崩溃报告里的描述与模组列表，方便快速判断。
            var summary = string.Empty;
            var description = System.Text.RegularExpressions.Regex.Match(text, @"Description:\s*(.+)");
            if (description.Success) summary = description.Groups[1].Value.Trim();
            var count = System.Text.RegularExpressions.Regex.Match(text, @"(\d+)\s+mods?\b");
            return new JObject
            {
                ["file"] = Path.GetFileName(path),
                ["findings"] = findings,
                ["summary"] = summary,
                ["modCount"] = count.Success ? count.Groups[1].Value : string.Empty,
                ["lines"] = text.Split('\n').Length
            };
        }
    }
}
