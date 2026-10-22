using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.Zip;
using Newtonsoft.Json.Linq;

namespace HalfMcLauncher
{
    /// <summary>
    /// electron/mods.ts 的 C# 移植：读取模组描述与图标、导入、启用/禁用、移到回收站。
    /// </summary>
    internal static class Mods
    {
        private static readonly string[] MetadataNames =
        {
            "fabric.mod.json", "quilt.mod.json", "META-INF/neoforge.mods.toml", "META-INF/mods.toml", "mcmod.info"
        };

        private static readonly Dictionary<string, string> IconCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        internal sealed class ModInfo
        {
            public string Name;
            public string Version;
            public List<string> Ids = new List<string>();
            public List<string> Depends = new List<string>();
            public string IconPath;
        }

        public static string ResourceDirectory(JObject instance, string folder)
        {
            var root = InstanceScanner.canonical(instance.Value<string>("root"));
            var gameDir = InstanceScanner.canonical(instance.Value<string>("gameDir"));
            if (!string.Equals(root, gameDir, StringComparison.OrdinalIgnoreCase) && !InstanceScanner.Inside(root, gameDir))
                throw new InvalidOperationException("游戏目录已越过登记根目录，请重新扫描");
            var directory = Path.Combine(gameDir, folder);
            Directory.CreateDirectory(directory);
            var resolved = InstanceScanner.canonical(directory);
            if (!InstanceScanner.Inside(gameDir, resolved))
                throw new InvalidOperationException("资源目录是外部链接，已阻止写入");
            return resolved;
        }

        public static string SafeFilename(string file)
        {
            if (string.IsNullOrEmpty(file) || file.Length > 200 || file != Path.GetFileName(file) ||
                Regex.IsMatch(file, "[<>:\"/\\\\|?*\x00-\x1f]") || Regex.IsMatch(file, "[. ]$") ||
                Regex.IsMatch(file, "^(con|prn|aux|nul|com[1-9]|lpt[1-9])(?:\\.|$)", RegexOptions.IgnoreCase))
                throw new InvalidOperationException("资源文件名无效");
            return file;
        }

        public static ModInfo Metadata(string file)
        {
            using (var stream = File.OpenRead(file))
            using (var zip = new ZipFile(stream))
            {
                var entry = MetadataNames.Select(name => zip.GetEntry(name)).FirstOrDefault(item => item != null);
                if (entry == null) return new ModInfo();
                if (entry.Size > 1048576) throw new InvalidOperationException("模组描述过大");
                string text;
                using (var input = zip.GetInputStream(entry))
                using (var reader = new StreamReader(input, Encoding.UTF8))
                    text = reader.ReadToEnd();

                var info = new ModInfo();
                if (entry.Name.EndsWith("mods.toml", StringComparison.OrdinalIgnoreCase))
                {
                    var blocks = Regex.Split(text, @"^\s*\[\[mods\]\]\s*$", RegexOptions.Multiline).Skip(1)
                        .Select(block => Regex.Split(block, @"^\s*\[", RegexOptions.Multiline)[0]).ToList();
                    var first = blocks.FirstOrDefault() ?? string.Empty;
                    info.Name = Regex.Match(first, "^\\s*displayName\\s*=\\s*[\"']([^\"']+)", RegexOptions.Multiline).Groups[1].Value;
                    info.Version = Regex.Match(first, "^\\s*version\\s*=\\s*[\"']([^\"']+)", RegexOptions.Multiline).Groups[1].Value;
                    info.Ids = blocks.Select(block => Regex.Match(block, "^\\s*modId\\s*=\\s*[\"']([^\"']+)", RegexOptions.Multiline).Groups[1].Value)
                        .Where(id => !string.IsNullOrEmpty(id)).ToList();
                    info.IconPath = Regex.Match(first, "^\\s*logoFile\\s*=\\s*[\"']([^\"']+)", RegexOptions.Multiline).Groups[1].Value;
                    foreach (Match dependency in Regex.Matches(text, @"\[\[dependencies\.[^\]]+\]\]([\s\S]*?)(?=\n\s*\[|\z)"))
                    {
                        var dependencyId = Regex.Match(dependency.Groups[1].Value, "modId\\s*=\\s*[\"']([^\"']+)").Groups[1].Value;
                        if (!string.IsNullOrEmpty(dependencyId)) info.Depends.Add(dependencyId);
                    }
                }
                else
                {
                    var json = JToken.Parse(text);
                    JToken meta = entry.Name == "quilt.mod.json" ? json["quilt_loader"] : entry.Name == "mcmod.info" ? (json is JArray array ? array.FirstOrDefault() : json) : json;
                    info.Name = (meta?["metadata"]?["name"] ?? meta?["name"] ?? meta?["id"])?.ToString() ?? string.Empty;
                    info.Version = meta?["version"]?.ToString() ?? string.Empty;
                    var id = (meta?["id"] ?? meta?["modid"])?.ToString();
                    if (!string.IsNullOrEmpty(id)) info.Ids.Add(id);
                    var icon = entry.Name == "quilt.mod.json" ? meta?["metadata"]?["icon"] : json["icon"];
                    info.IconPath = IconValue(icon);
                    var depends = meta?["depends"] as JObject;
                    if (depends != null) foreach (var property in depends.Properties()) info.Depends.Add(property.Name);
                    if (meta?["depends"] is JArray dependencyArray)
                        foreach (var item in dependencyArray)
                        {
                            if (item.Type == JTokenType.String) info.Depends.Add(item.ToString());
                            else if (item is JObject obj && obj.Value<string>("id") != null) info.Depends.Add(obj.Value<string>("id"));
                        }
                }
                info.Name = (info.Name ?? string.Empty).Substring(0, Math.Min(200, (info.Name ?? string.Empty).Length));
                info.Version = (info.Version ?? string.Empty).Substring(0, Math.Min(100, (info.Version ?? string.Empty).Length));
                return info;
            }
        }

        private static string IconValue(JToken token)
        {
            if (token == null) return null;
            if (token.Type == JTokenType.String) return token.ToString();
            if (token is JArray array) return array.FirstOrDefault(item => item.Type == JTokenType.String)?.ToString();
            if (token is JObject obj) return obj.Properties().Select(item => item.Value).FirstOrDefault(value => value.Type == JTokenType.String)?.ToString();
            return null;
        }

        public static string Icon(string file, ModInfo info, string cacheKey)
        {
            if (!string.IsNullOrEmpty(cacheKey) && IconCache.TryGetValue(cacheKey, out var cached)) return cached;
            var candidates = new List<string>();
            if (!string.IsNullOrEmpty(info?.IconPath)) candidates.Add(info.IconPath);
            var modId = info?.Ids?.FirstOrDefault();
            if (!string.IsNullOrEmpty(modId)) candidates.Add("assets/" + modId + "/icon.png");
            candidates.Add("pack.png");
            string result = null;
            try
            {
                using (var stream = File.OpenRead(file))
                using (var zip = new ZipFile(stream))
                {
                    var entry = candidates.Distinct().Select(name => zip.GetEntry(name)).FirstOrDefault(item => item != null);
                    if (entry != null && entry.Size <= 524288)
                    {
                        using (var input = zip.GetInputStream(entry))
                        using (var memory = new MemoryStream())
                        {
                            input.CopyTo(memory);
                            var mime = MimeFor(entry.Name);
                            result = "data:" + mime + ";base64," + Convert.ToBase64String(memory.ToArray());
                        }
                    }
                }
            }
            catch { }
            if (!string.IsNullOrEmpty(cacheKey)) IconCache[cacheKey] = result;
            return result;
        }

        private static string MimeFor(string name)
        {
            var extension = Path.GetExtension(name).ToLowerInvariant();
            if (extension == ".jpg" || extension == ".jpeg") return "image/jpeg";
            if (extension == ".gif") return "image/gif";
            if (extension == ".webp") return "image/webp";
            return "image/png";
        }

        public static JArray List(JObject instance)
        {
            var directory = Path.Combine(instance.Value<string>("gameDir"), "mods");
            var result = new JArray();
            if (!Directory.Exists(directory)) return result;
            var rows = new List<Tuple<JObject, ModInfo>>();
            var namesById = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var builtin = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "minecraft", "java", "fabricloader", "forge", "neoforge", "quilt_loader" };

            foreach (var file in Directory.EnumerateFiles(directory).Where(name => Regex.IsMatch(Path.GetFileName(name), "\\.jar(?:\\.disabled)?$", RegexOptions.IgnoreCase)))
            {
                var info = new FileInfo(file);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                ModInfo meta = new ModInfo();
                string issue = null;
                try { meta = Metadata(file); }
                catch (Exception error) { issue = "无法读取模组信息：" + error.Message; }
                var icon = Icon(file, meta, file + ":" + info.Length + ":" + info.LastWriteTimeUtc.Ticks);
                var name = string.IsNullOrEmpty(meta.Name) ? Regex.Replace(info.Name, "\\.jar(?:\\.disabled)?$", "", RegexOptions.IgnoreCase) : meta.Name;
                var row = new JObject
                {
                    ["file"] = info.Name,
                    ["name"] = name,
                    ["version"] = meta.Version ?? string.Empty,
                    ["enabled"] = !info.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase),
                    ["size"] = info.Length,
                    ["issue"] = issue,
                    ["icon"] = icon,
                    ["ids"] = new JArray(meta.Ids)
                };
                rows.Add(Tuple.Create(row, meta));
                foreach (var id in meta.Ids) if (!namesById.ContainsKey(id)) namesById[id] = name;
            }

            foreach (var row in rows)
            {
                var links = new JArray();
                foreach (var dependency in row.Item2.Depends.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (builtin.Contains(dependency)) continue;
                    var dependencyName = namesById.TryGetValue(dependency, out var resolved) ? resolved : dependency;
                    links.Add(new JObject { ["id"] = dependency, ["name"] = dependencyName, ["kind"] = "depends", ["missing"] = !namesById.ContainsKey(dependency) });
                }
                foreach (var other in rows)
                {
                    if (ReferenceEquals(other, row)) continue;
                    if (!other.Item2.Depends.Any(dep => row.Item2.Ids.Contains(dep, StringComparer.OrdinalIgnoreCase))) continue;
                    links.Add(new JObject { ["id"] = other.Item1.Value<string>("file"), ["name"] = other.Item1.Value<string>("name"), ["kind"] = "usedBy" });
                }
                row.Item1["links"] = links;
            }

            var sorted = rows.Select(row => row.Item1).OrderBy(item => item.Value<string>("name"), StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), true)).ToList();
            return new JArray(sorted);
        }

        public static void Import(JObject instance, IEnumerable<string> files)
        {
            var directory = ResourceDirectory(instance, "mods");
            var selection = files.ToList();
            foreach (var file in selection)
            {
                SafeFilename(Path.GetFileName(file));
                if (!file.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("只能导入 JAR 模组");
                Metadata(file);
                if (File.Exists(Path.Combine(directory, Path.GetFileName(file)))) throw new InvalidOperationException("已存在同名文件：" + Path.GetFileName(file));
            }
            foreach (var file in selection)
            {
                var target = Path.Combine(directory, Path.GetFileName(file));
                using (var source = File.OpenRead(file))
                using (var destination = new FileStream(target, FileMode.CreateNew))
                    source.CopyTo(destination);
            }
        }

        public static void Toggle(JObject instance, string file)
        {
            var target = ModPath(instance, file);
            var next = file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? target.Substring(0, target.Length - 9) : target + ".disabled";
            var directory = Path.GetDirectoryName(target);
            try
            {
                var probe = Path.Combine(directory, ".half-write-probe");
                File.WriteAllText(probe, string.Empty);
                File.Delete(probe);
            }
            catch (Exception error)
            {
                Diagnostics.Write("mods.write probe failed: " + error.Message + " => " + directory);
                throw new InvalidOperationException("mods 目录不可写，请检查文件夹权限或以管理员身份运行启动器：" + directory, error);
            }
            try
            {
                if (File.Exists(next))
                {
                    File.SetAttributes(next, FileAttributes.Normal);
                    File.Delete(next);
                }
                if ((File.GetAttributes(target) & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(target, FileAttributes.Normal);
                File.Move(target, next);
            }
            catch (Exception error) when (error is UnauthorizedAccessException || error is IOException)
            {
                Diagnostics.Write("mods.toggle move failed: " + error.GetType().Name + ": " + error.Message + " => " + target);
                try
                {
                    File.Copy(target, next, true);
                    File.SetAttributes(next, FileAttributes.Normal);
                    File.Delete(target);
                }
                catch (Exception fallback)
                {
                    Diagnostics.Write("mods.toggle copy fallback failed: " + fallback.Message);
                    throw new InvalidOperationException("无法修改模组文件，请确认 mods 目录可写、文件没有被其他程序占用：" + target, fallback);
                }
            }
        }

        public static void Reveal(JObject instance, string file)
        {
            var target = ModPath(instance, file);
            if (!File.Exists(target)) throw new InvalidOperationException("文件不存在：" + file);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + target + "\"") { UseShellExecute = true });
        }

        public static JArray Batch(JObject instance, string action, IEnumerable<string> files)
        {
            foreach (var file in files)
            {
                var enabled = !file.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                if (action == "enable" && !enabled) Toggle(instance, file);
                else if (action == "disable" && enabled) Toggle(instance, file);
                else if (action == "delete") Delete(instance, file);
            }
            return List(instance);
        }
        public static void Delete(JObject instance, string file)
        {
            var target = ModPath(instance, file);
            Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(target,
                Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
        }

        public static string ModPath(JObject instance, string file)
        {
            SafeFilename(file);
            if (!Regex.IsMatch(file, "\\.jar(?:\\.disabled)?$", RegexOptions.IgnoreCase)) throw new InvalidOperationException("请选择 JAR 模组文件");
            var directory = ResourceDirectory(instance, "mods");
            var target = Path.Combine(directory, file);
            var info = new FileInfo(target);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("模组文件已发生变化，请刷新列表");
            return target;
        }
    }
}
