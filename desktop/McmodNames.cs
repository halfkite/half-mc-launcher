using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace HalfMcLauncher
{
    /// <summary>
    /// 从 MC百科 查询模组中文名：只在确有对应条目时返回，不做机器翻译。
    /// 结果缓存在 data/mcmod-names.json，避免重复请求。
    /// </summary>
    internal static class McmodNames
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(6);
        private static readonly Dictionary<string, string> Cache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Misses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object GateLock = new object();
        private static bool loaded;

        private static string CachePath => Path.Combine(AppPaths.DataDir, "mcmod-names.json");

        private static void EnsureLoaded()
        {
            if (loaded) return;
            lock (GateLock)
            {
                if (loaded) return;
                loaded = true;
                try
                {
                    if (File.Exists(CachePath))
                    {
                        var json = JObject.Parse(File.ReadAllText(CachePath));
                        foreach (var property in json.Properties())
                            if (property.Value.Type == JTokenType.String && !string.IsNullOrEmpty(property.Value.ToString()))
                                Cache[property.Name] = property.Value.ToString();
                    }
                }
                catch { }
            }
        }

        private static void Save()
        {
            try
            {
                lock (GateLock)
                {
                    var json = new JObject();
                    foreach (var pair in Cache) json[pair.Key] = pair.Value;
                    Directory.CreateDirectory(AppPaths.DataDir);
                    File.WriteAllText(CachePath, json.ToString(Newtonsoft.Json.Formatting.Indented));
                }
            }
            catch { }
        }

        private static string Decode(string value) => WebUtility.HtmlDecode(value ?? string.Empty).Trim();
        private static string Normalize(string value) => Regex.Replace((value ?? string.Empty).ToLowerInvariant(), "[^a-z0-9]+", string.Empty);

        private static async Task<string> SearchOne(string english, CancellationToken ct)
        {
            try
            {
                var url = "https://search.mcmod.cn/s?key=" + Uri.EscapeDataString(english);
                using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                {
                    request.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
                    request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
                    using (var response = await Http.SendAsync(request, ct).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode) return null;
                        var html = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        foreach (Match match in Regex.Matches(html, "<a[^>]*href=\"(?:https?:)?//www\\.mcmod\\.cn/class/(\\d+)\\.html\"[^>]*>(.*?)</a>", RegexOptions.Singleline))
                        {
                            var text = Regex.Replace(match.Groups[2].Value, "<[^>]+>", string.Empty);
                            var decoded = Decode(text);
                            var open = decoded.LastIndexOf('(');
                            var close = decoded.LastIndexOf(')');
                            if (open <= 0 || close <= open) continue;
                            var chinese = decoded.Substring(0, open).Trim();
                            var alias = decoded.Substring(open + 1, close - open - 1).Trim();
                            if (string.IsNullOrEmpty(chinese)) continue;
                            if (string.Equals(Normalize(alias), Normalize(english), StringComparison.OrdinalIgnoreCase)) return chinese;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        public static async Task Apply(JArray projects, CancellationToken ct)
        {
            EnsureLoaded();
            var targets = projects.OfType<JObject>()
                .Select(project => new { Project = project, Name = project.Value<string>("name") ?? string.Empty })
                .Where(item => item.Name.Length > 0 && !item.Name.Any(character => character > 0x4e00))
                .Take(24)
                .ToList();
            if (targets.Count == 0) return;

            foreach (var target in targets)
            {
                if (Cache.TryGetValue(target.Name, out var known))
                {
                    target.Project["nameZh"] = known;
                    continue;
                }
                if (Misses.Contains(target.Name)) continue;
            }

            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(6));
                var pending = targets.Where(item => !Cache.ContainsKey(item.Name) && !Misses.Contains(item.Name)).ToList();
                var changed = false;
                await Task.WhenAll(pending.Select(async item =>
                {
                    await Gate.WaitAsync(timeout.Token).ConfigureAwait(false);
                    try
                    {
                        var chinese = await SearchOne(item.Name, timeout.Token).ConfigureAwait(false);
                        lock (Cache)
                        {
                            if (!string.IsNullOrEmpty(chinese))
                            {
                                Cache[item.Name] = chinese;
                                item.Project["nameZh"] = chinese;
                                changed = true;
                            }
                            else Misses.Add(item.Name);
                        }
                    }
                    catch { Misses.Add(item.Name); }
                    finally { Gate.Release(); }
                })).ConfigureAwait(false);
                if (changed) Save();
            }
        }
    }
}
