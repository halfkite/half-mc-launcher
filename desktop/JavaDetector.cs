using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace HalfMcLauncher
{
    /// <summary>
    /// electron/java.ts 的 C# 移植：从 PATH、JAVA_HOME、常见安装目录与用户登记的搜索目录里
    /// 找出可执行的 java.exe，并用 -XshowSettings:properties 读取版本与架构。
    /// </summary>
    internal static class JavaDetector
    {
        private static readonly Regex VersionPattern = new Regex(@"(?:java\.version\s*=\s*|version\s+"")([^\s""]+)", RegexOptions.Compiled);
        private static readonly Regex ArchPattern = new Regex(@"os\.arch\s*=\s*(\S+)", RegexOptions.Compiled);

        public static List<JObject> Discover(IEnumerable<string> extra, IEnumerable<string> folders)
        {
            var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in extra.Where(item => !string.IsNullOrWhiteSpace(item))) files.Add(file);

            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(Path.PathSeparator))
                if (!string.IsNullOrWhiteSpace(dir)) files.Add(Path.Combine(dir.Trim('"'), "java.exe"));

            var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (!string.IsNullOrWhiteSpace(javaHome)) files.Add(Path.Combine(javaHome, "bin", "java.exe"));

            var programFiles = Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files";
            var roots = new List<string>
            {
                Path.Combine(programFiles, "Java"),
                Path.Combine(programFiles, "Eclipse Adoptium"),
                Path.Combine(programFiles, "Microsoft"),
                Path.Combine(programFiles, "Zulu"),
                Path.Combine(Environment.GetEnvironmentVariable("APPDATA") ?? string.Empty, ".minecraft", "runtime")
            };
            roots.AddRange(folders.Where(item => !string.IsNullOrWhiteSpace(item)));

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var directories = 0;
            Action<string, int> walk = null;
            walk = (dir, depth) =>
            {
                if (directories >= 10000) return;
                string key;
                try { key = Path.GetFullPath(dir).ToLowerInvariant(); } catch { return; }
                if (!visited.Add(key)) return;
                directories++;

                files.Add(Path.Combine(dir, "bin", "java.exe"));
                files.Add(Path.Combine(dir, "java.exe"));

                IEnumerable<string> children;
                try { children = Directory.EnumerateDirectories(dir); } catch { return; }
                foreach (var child in children)
                {
                    string name;
                    try { name = Path.GetFileName(child); } catch { continue; }
                    if (name.StartsWith(".")) continue;
                    files.Add(Path.Combine(child, "bin", "java.exe"));
                    if (depth > 0) walk(child, depth - 1);
                }
            };
            foreach (var root in roots) walk(root, folders.Contains(root) ? 5 : 2);

            var candidates = files.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var results = new List<JObject>();
            foreach (var batch in Batch(candidates, 6))
            {
                var probes = batch.Select(Probe).ToArray();
                Task.WaitAll(probes);
                foreach (var probe in probes)
                {
                    var info = probe.Status == TaskStatus.RanToCompletion ? probe.Result : null;
                    if (info != null) results.Add(info);
                }
            }

            return results
                .GroupBy(item => item.Value<string>("path"), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderByDescending(item => item.Value<int>("major"))
                .ToList();
        }

        public static Task<JObject> Probe(string file)
        {
            return Task.Run(() =>
            {
                var start = new ProcessStartInfo(file, "-XshowSettings:properties -version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                using (var process = new Process { StartInfo = start })
                {
                    var output = new StringBuilder();
                    process.OutputDataReceived += (sender, args) => { if (args.Data != null) lock (output) output.AppendLine(args.Data); };
                    process.ErrorDataReceived += (sender, args) => { if (args.Data != null) lock (output) output.AppendLine(args.Data); };
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();
                    if (!process.WaitForExit(10000))
                    {
                        try { process.Kill(); } catch { }
                        throw new TimeoutException("Java 检测超时：" + file);
                    }
                    process.WaitForExit();
                    string text;
                    lock (output) text = output.ToString();
                    var version = VersionPattern.Match(text);
                    if (!version.Success) throw new InvalidOperationException("无法识别 Java 版本：" + file);
                    var raw = version.Groups[1].Value;
                    var parts = raw.Split(new[] { '.', '_', '+', '-' });
                    var major = 0;
                    int.TryParse(parts[0], out major);
                    if (major == 1 && parts.Length > 1) int.TryParse(parts[1], out major);
                    var arch = ArchPattern.Match(text);
                    return new JObject
                    {
                        ["path"] = file,
                        ["version"] = raw,
                        ["major"] = major,
                        ["arch"] = arch.Success ? arch.Groups[1].Value : "unknown"
                    };
                }
            });
        }

        private static IEnumerable<string[]> Batch(List<string> source, int size)
        {
            for (var index = 0; index < source.Count; index += size)
                yield return source.Skip(index).Take(size).ToArray();
        }
    }
}
