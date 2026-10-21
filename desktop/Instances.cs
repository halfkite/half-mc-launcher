using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

namespace HalfMcLauncher
{
    internal sealed class ScanResult
    {
        public List<JObject> Instances = new List<JObject>();
        public List<string> Warnings = new List<string>();
    }

    /// <summary>
    /// electron/instances.ts 的 C# 移植：登记目录 → 查找含 versions 的游戏根 → 读取版本描述。
    /// 只做只读扫描，不修改用户的游戏目录。
    /// </summary>
    internal static class InstanceScanner
    {
        public static string InstanceId(string root, string version)
        {
            var key = canonical(root).ToLowerInvariant() + "\0" + version;
            using (var sha = SHA256.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                var builder = new StringBuilder(24);
                foreach (var b in hash.Take(12)) builder.Append(b.ToString("x2"));
                return builder.ToString();
            }
        }

        public static string canonical(string path)
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full) ?? string.Empty;
            return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
        }

        public static bool Inside(string parent, string child)
        {
            var relative = GetRelativePath(parent, child);
            return !string.IsNullOrEmpty(relative) && !relative.StartsWith(".." + Path.DirectorySeparatorChar) && relative != ".." && !Path.IsPathRooted(relative);
        }

        private static string GetRelativePath(string from, string to)
        {
            var fromUri = new Uri(AppendSeparator(Path.GetFullPath(from)));
            var toUri = new Uri(Path.GetFullPath(to));
            return Uri.UnescapeDataString(fromUri.MakeRelativeUri(toUri).ToString()).Replace('/', Path.DirectorySeparatorChar);
        }

        private static string AppendSeparator(string path)
        {
            return path.EndsWith(Path.DirectorySeparatorChar.ToString()) ? path : path + Path.DirectorySeparatorChar;
        }

        public static List<string> FindRoots(string directory)
        {
            var roots = new List<string>();
            var baseDir = canonical(directory);
            Action<string> consider = dir =>
            {
                if (Directory.Exists(Path.Combine(dir, "versions")))
                {
                    var resolved = canonical(dir);
                    if (!roots.Any(item => string.Equals(item, resolved, StringComparison.OrdinalIgnoreCase))) roots.Add(resolved);
                }
            };

            consider(baseDir);
            consider(Path.Combine(baseDir, ".minecraft"));
            if (string.Equals(Path.GetFileName(baseDir), "versions", StringComparison.OrdinalIgnoreCase))
                consider(Path.GetDirectoryName(baseDir));

            if (roots.Count == 0)
            {
                foreach (var child in Directory.EnumerateDirectories(baseDir).Take(2000))
                {
                    consider(child);
                    consider(Path.Combine(child, ".minecraft"));
                }
            }
            return roots;
        }

        public static ScanResult Scan(JObject state)
        {
            var result = new ScanResult();
            var seen = new HashSet<string>();
            var hidden = new HashSet<string>((state["hidden"] as JArray ?? new JArray()).Select(token => token.ToString()));
            var options = state["options"] as JObject ?? new JObject();
            var settings = state["settings"] as JObject ?? new JObject();
            var lastPlayed = state["lastPlayed"] as JObject ?? new JObject();
            var sources = state["sources"] as JArray ?? new JArray();

            foreach (var sourceToken in sources)
            {
                var source = sourceToken as JObject;
                if (source == null) continue;
                var label = source.Value<string>("label") ?? source.Value<string>("path") ?? "目录";
                var path = source.Value<string>("path");
                var sourceId = source.Value<string>("id") ?? string.Empty;
                if (string.IsNullOrWhiteSpace(path)) continue;

                List<string> roots;
                try { roots = FindRoots(path); }
                catch (Exception error) { result.Warnings.Add(label + "：" + error.Message); continue; }
                if (roots.Count == 0) result.Warnings.Add(label + "：尚未发现含 versions 的 Minecraft 目录。");

                foreach (var root in roots)
                {
                    IEnumerable<string> entries;
                    try { entries = Directory.EnumerateDirectories(Path.Combine(root, "versions")); }
                    catch (Exception error) { result.Warnings.Add(root + "：" + error.Message); continue; }

                    foreach (var versionDir in entries)
                    {
                        var name = Path.GetFileName(versionDir);
                        var id = InstanceId(root, name);
                        if (seen.Contains(id) || hidden.Contains(id)) continue;
                        seen.Add(id);

                        var jsonFile = Path.Combine(versionDir, name + ".json");
                        if (!File.Exists(jsonFile)) continue;

                        JObject meta = null;
                        string issue = null;
                        try
                        {
                            meta = JObject.Parse(File.ReadAllText(jsonFile, Encoding.UTF8));
                            if (string.IsNullOrEmpty(meta.Value<string>("id")) || (string.IsNullOrEmpty(meta.Value<string>("mainClass")) && string.IsNullOrEmpty(meta.Value<string>("inheritsFrom"))))
                                issue = "版本描述缺少 id、mainClass 或父版本";
                        }
                        catch { issue = "版本 JSON 已损坏，无法启动"; }

                        var libraries = (meta?["libraries"] as JArray ?? new JArray())
                            .Select(library => library?["name"]?.ToString() ?? string.Empty).ToList();
                        var loader = DetectLoader(libraries);

                        var option = options[id] as JObject ?? new JObject();
                        var isolated = option["isolated"] != null
                            ? option.Value<bool>("isolated")
                            : Directory.Exists(Path.Combine(versionDir, "mods")) || Directory.Exists(Path.Combine(versionDir, "saves"));

                        var entry = new JObject
                        {
                            ["id"] = id,
                            ["name"] = option.Value<string>("name") ?? name,
                            ["group"] = option.Value<string>("group") ?? string.Empty,
                            ["favorite"] = option.Value<bool?>("favorite") ?? false,
                            ["memory"] = option.Value<int?>("memory") ?? settings.Value<int?>("memory") ?? 4096,
                            ["java"] = option.Value<string>("java") ?? string.Empty,
                            ["isolated"] = isolated,
                            ["protected"] = option.Value<bool?>("protected") ?? false,
                            ["memoryMode"] = option.Value<string>("memoryMode") ?? "inherit",
                            ["sourceId"] = sourceId,
                            ["root"] = root,
                            ["version"] = name,
                            ["minecraft"] = meta?.Value<string>("inheritsFrom") ?? meta?.Value<string>("id") ?? name,
                            ["loader"] = loader,
                            ["gameDir"] = isolated ? versionDir : root,
                            ["issue"] = issue,
                            ["lastPlayed"] = lastPlayed[id]?.ToString()
                        };
                        result.Instances.Add(entry);
                    }
                }
            }

            var comparer = StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), true);
            result.Instances.Sort((a, b) =>
            {
                var favorite = (b.Value<bool?>("favorite") ?? false).CompareTo(a.Value<bool?>("favorite") ?? false);
                if (favorite != 0) return favorite;
                return comparer.Compare(a.Value<string>("name"), b.Value<string>("name"));
            });
            return result;
        }

        private static string DetectLoader(List<string> libraries)
        {
            if (libraries.Any(name => name.StartsWith("net.fabricmc:fabric-loader:", StringComparison.OrdinalIgnoreCase))) return "Fabric";
            if (libraries.Any(name => name.StartsWith("net.neoforged:", StringComparison.OrdinalIgnoreCase))) return "NeoForge";
            if (libraries.Any(name => name.StartsWith("net.minecraftforge:", StringComparison.OrdinalIgnoreCase))) return "Forge";
            if (libraries.Any(name => name.StartsWith("org.quiltmc:", StringComparison.OrdinalIgnoreCase))) return "Quilt";
            if (libraries.Any(name => name.StartsWith("com.mumfrey:liteloader:", StringComparison.OrdinalIgnoreCase))) return "LiteLoader";
            if (libraries.Any(name => name.StartsWith("optifine:", StringComparison.OrdinalIgnoreCase))) return "OptiFine";
            return "Vanilla";
        }
    }
}
