using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.Zip;
using Newtonsoft.Json.Linq;

namespace HalfMcLauncher
{
    /// <summary>
    /// 模组翻译：扫描模组 jar 内的 en_us.json，用在线翻译生成 zh_cn 语言文件，
    /// 打包成资源包放进 resourcepacks，由游戏资源包系统覆盖显示文本。
    /// </summary>
    internal static class ModTranslation
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        private sealed class Source
        {
            public string ModId;
            public string File;
            public Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        private static List<Source> ScanSources(JObject instance)
        {
            var mods = Path.Combine(InstanceScanner.canonical(instance.Value<string>("gameDir")), "mods");
            var result = new List<Source>();
            if (!Directory.Exists(mods)) return result;
            foreach (var jar in Directory.EnumerateFiles(mods, "*.jar").Take(300))
            {
                try
                {
                    using (var zip = new ICSharpCode.SharpZipLib.Zip.ZipFile(File.OpenRead(jar)))
                    {
                        foreach (ZipEntry entry in zip)
                        {
                            if (!entry.IsFile || entry.Size <= 0 || entry.Size > 1024 * 1024) continue;
                            var match = System.Text.RegularExpressions.Regex.Match(entry.Name, "^assets/([^/]+)/lang/en_us\\.json$");
                            if (!match.Success) continue;
                            using (var input = zip.GetInputStream(entry))
                            using (var reader = new StreamReader((Stream)input, Encoding.UTF8))
                            {
                                var json = JObject.Parse(reader.ReadToEnd());
                                var source = new Source { ModId = match.Groups[1].Value, File = Path.GetFileName(jar) };
                                foreach (var property in json.Properties())
                                    if (property.Value.Type == JTokenType.String) source.Values[property.Name] = property.Value.ToString();
                                if (source.Values.Count > 0) result.Add(source);
                            }
                        }
                    }
                }
                catch { }
            }
            return result;
        }

        public static JObject Preview(JObject instance)
        {
            var sources = ScanSources(instance);
            return new JObject
            {
                ["mods"] = sources.Count,
                ["keys"] = sources.Sum(item => item.Values.Count)
            };
        }

        private static async Task<string> TranslateOne(string text)
        {
            var url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=en&tl=zh-CN&dt=t&q=" + Uri.EscapeDataString(text);
            using (var response = await Http.GetAsync(url).ConfigureAwait(false))
            {
                if (!response.IsSuccessStatusCode) return null;
                var json = JArray.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                var parts = json.Count > 0 ? json[0] as JArray : null;
                if (parts == null) return null;
                var builder = new StringBuilder();
                foreach (var part in parts.OfType<JArray>())
                {
                    var value = part.FirstOrDefault()?.ToString();
                    if (!string.IsNullOrEmpty(value)) builder.Append(value);
                }
                var translated = builder.ToString().Trim();
                return string.IsNullOrEmpty(translated) ? null : translated;
            }
        }

        public static async Task<JObject> Translate(JObject instance, int limit, Action<int, int> progress, CancellationToken ct)
        {
            var sources = ScanSources(instance);
            var unique = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in sources)
                foreach (var pair in source.Values)
                {
                    var value = pair.Value?.Trim() ?? string.Empty;
                    if (value.Length == 0 || value.Length > 300) continue;
                    if (value.Contains("%") || value.Contains("\n")) continue;
                    if (seen.Add(value)) unique.Add(value);
                }
            if (limit > 0 && unique.Count > limit) unique = unique.Take(limit).ToList();

            var translated = new Dictionary<string, string>(StringComparer.Ordinal);
            var done = 0;
            var failures = 0;
            using (var gate = new SemaphoreSlim(6))
            {
                var tasks = unique.Select(async text =>
                {
                    await gate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        var value = await TranslateOne(text).ConfigureAwait(false);
                        lock (translated)
                        {
                            if (value != null) translated[text] = value;
                            done++;
                            if (value == null) failures++;
                            if (done % 5 == 0 || done == unique.Count) progress?.Invoke(done, unique.Count);
                        }
                    }
                    catch { lock (translated) { failures++; } }
                    finally { gate.Release(); }
                }).ToArray();
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }

            var pack = new JObject
            {
                ["pack"] = new JObject
                {
                    ["pack_format"] = 34,
                    ["description"] = "Half 模组翻译 · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm")
                }
            };
            var root = InstanceScanner.canonical(instance.Value<string>("gameDir"));
            var directory = Path.Combine(root, "resourcepacks");
            Directory.CreateDirectory(directory);
            var name = "Half-模组翻译-" + DateTime.Now.ToString("MMdd-HHmm") + ".zip";
            var path = Path.Combine(directory, name);
            var modsWritten = 0;
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                void Write(string entryName, string content)
                {
                    var entry = archive.CreateEntry(entryName, CompressionLevel.Optimal);
                    using (var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                        writer.Write(content);
                }
                Write("pack.mcmeta", pack.ToString(Newtonsoft.Json.Formatting.Indented));
                foreach (var source in sources)
                {
                    var target = new JObject();
                    foreach (var pair in source.Values)
                    {
                        var value = pair.Value?.Trim() ?? string.Empty;
                        if (translated.TryGetValue(value, out var zh)) target[pair.Key] = zh;
                    }
                    if (target.Count == 0) continue;
                    Write("assets/" + source.ModId + "/lang/zh_cn.json", target.ToString(Newtonsoft.Json.Formatting.Indented));
                    modsWritten++;
                }
            }

            return new JObject
            {
                ["path"] = path,
                ["name"] = name,
                ["mods"] = modsWritten,
                ["translated"] = translated.Count,
                ["attempted"] = done,
                ["failed"] = failures,
                ["keys"] = sources.Sum(item => item.Values.Count)
            };
        }
    }
}
