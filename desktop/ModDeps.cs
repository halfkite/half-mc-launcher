using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ICSharpCode.SharpZipLib.Zip;
using Newtonsoft.Json.Linq;

namespace HalfMcLauncher
{
    /// <summary>
    /// 模组依赖检查：读取 fabric.mod.json / quilt.mod.json 的 id 与 depends，
    /// 报告缺失依赖和重复安装的模组。
    /// </summary>
    internal static class ModDeps
    {
        private static readonly HashSet<string> Builtin = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "minecraft", "java", "fabricloader", "fabric-loader", "quilt_loader", "quiltloader", "forge", "neoforge"
        };

        private sealed class Entry
        {
            public string File;
            public string Id;
            public string Name;
            public string Version;
            public JObject Depends;
        }

        public static JObject Check(JObject instance)
        {
            var directory = Path.Combine(InstanceScanner.canonical(instance.Value<string>("gameDir")), "mods");
            var entries = new List<Entry>();
            var provided = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(directory))
            {
                foreach (var jar in Directory.EnumerateFiles(directory, "*.jar").Take(500))
                {
                    var entry = new Entry { File = Path.GetFileName(jar) };
                    try
                    {
                        using (var zip = new ZipFile(File.OpenRead(jar)))
                        {
                            var item = zip.GetEntry("fabric.mod.json") ?? zip.GetEntry("quilt.mod.json");
                            if (item == null || item.Size > 4 * 1024 * 1024) continue;
                            using (var input = zip.GetInputStream(item))
                            using (var reader = new StreamReader(input, System.Text.Encoding.UTF8))
                            {
                                var meta = JObject.Parse(reader.ReadToEnd());
                                var quilt = meta["quilt_loader"] as JObject;
                                entry.Id = Safe(meta.Value<string>("id") ?? quilt?.Value<string>("id"));
                                entry.Name = meta.Value<string>("name") ?? entry.Id ?? Path.GetFileNameWithoutExtension(jar);
                                entry.Version = meta.Value<string>("version") ?? quilt?.Value<string>("version") ?? string.Empty;
                                entry.Depends = (meta["depends"] as JObject) ?? (quilt?["depends"] as JObject);
                            }
                            var nestedIndex = 0;
                            foreach (ZipEntry nested in zip)
                            {
                                if (nestedIndex++ > 120) break;
                                if (!nested.IsFile || nested.Size <= 0 || nested.Size > 4 * 1024 * 1024) continue;
                                if (!nested.Name.StartsWith("META-INF/jars/", StringComparison.OrdinalIgnoreCase)) continue;
                                if (!nested.Name.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) continue;
                                try
                                {
                                    using (var input = zip.GetInputStream(nested))
                                    using (var memory = new MemoryStream())
                                    {
                                        input.CopyTo(memory);
                                        memory.Seek(0, SeekOrigin.Begin);
                                        using (var inner = new ZipFile(memory))
                                        {
                                            var innerMeta = inner.GetEntry("fabric.mod.json") ?? inner.GetEntry("quilt.mod.json");
                                            if (innerMeta == null || innerMeta.Size > 2 * 1024 * 1024) continue;
                                            using (var innerInput = inner.GetInputStream(innerMeta))
                                            using (var innerReader = new StreamReader(innerInput, System.Text.Encoding.UTF8))
                                            {
                                                var nestedMeta = JObject.Parse(innerReader.ReadToEnd());
                                                var nestedId = nestedMeta.Value<string>("id") ?? (nestedMeta["quilt_loader"] as JObject)?.Value<string>("id");
                                                if (!string.IsNullOrEmpty(nestedId)) provided.Add(nestedId);
                                            }
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    catch { continue; }
                    if (!string.IsNullOrEmpty(entry.Id)) entries.Add(entry);
                }
            }

            var installed = new Dictionary<string, List<Entry>>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
            {
                if (!installed.TryGetValue(entry.Id, out var list)) installed[entry.Id] = list = new List<Entry>();
                list.Add(entry);
            }

            var missing = new JArray();
            foreach (var entry in entries)
            {
                foreach (var dependency in entry.Depends?.Properties() ?? Enumerable.Empty<JProperty>())
                {
                    var id = dependency.Name;
                    if (Builtin.Contains(id) || string.IsNullOrWhiteSpace(id)) continue;
                    if (installed.ContainsKey(id) || provided.Contains(id)) continue;
                    missing.Add(new JObject
                    {
                        ["file"] = entry.File,
                        ["id"] = entry.Id,
                        ["name"] = entry.Name,
                        ["dep"] = id,
                        ["range"] = dependency.Value.Type == JTokenType.String ? dependency.Value.ToString() : dependency.Value.ToString(Newtonsoft.Json.Formatting.None)
                    });
                }
            }

            var duplicates = new JArray();
            foreach (var pair in installed.Where(item => item.Value.Count > 1))
                duplicates.Add(new JObject
                {
                    ["id"] = pair.Key,
                    ["files"] = new JArray(pair.Value.Select(item => item.File))
                });

            return new JObject
            {
                ["ok"] = missing.Count == 0 && duplicates.Count == 0,
                ["checked"] = entries.Count,
                ["mods"] = new JArray(entries.Select(item => new JObject
                {
                    ["file"] = item.File,
                    ["id"] = item.Id,
                    ["name"] = item.Name,
                    ["version"] = item.Version
                })),
                ["missing"] = missing,
                ["duplicates"] = duplicates
            };
        }

        private static string Safe(string value) => value ?? string.Empty;
    }
}
