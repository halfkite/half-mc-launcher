using System;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace HalfMcLauncher
{
    /// <summary>
    /// 启动器更新检查：读取设置里的更新清单 URL（JSON 文件或 GitHub Release），
    /// 比较版本并给出更新说明与跳转地址。未配置时返回 configured=false。
    /// </summary>
    internal static class Updates
    {
        private static string Source => Program.MainWindow?.Store.Settings.Value<string>("updateUrl") ?? string.Empty;

        private static int[] Parts(string version)
        {
            return Regex.Matches((version ?? string.Empty).ToLowerInvariant(), @"\d+")
                .Cast<Match>().Select(match => int.TryParse(match.Value, out var value) ? value : 0).ToArray();
        }

        private static int Compare(string left, string right)
        {
            var a = Parts(left);
            var b = Parts(right);
            var length = Math.Max(a.Length, b.Length);
            for (var index = 0; index < length; index++)
            {
                var x = index < a.Length ? a[index] : 0;
                var y = index < b.Length ? b[index] : 0;
                if (x != y) return x > y ? 1 : -1;
            }
            return 0;
        }

        public static JObject Info()
        {
            return new JObject
            {
                ["current"] = Program.Version,
                ["source"] = Source,
                ["configured"] = !string.IsNullOrWhiteSpace(Source)
            };
        }

        public static void SetSource(string url)
        {
            if (Program.MainWindow == null) throw new InvalidOperationException("启动器尚未就绪");
            Program.MainWindow.Store.Settings["updateUrl"] = (url ?? string.Empty).Trim();
            Program.MainWindow.Store.Save();
        }

        public static async Task<JObject> CheckAsync(CancellationToken ct)
        {
            var source = Source.Trim();
            if (string.IsNullOrEmpty(source))
                return new JObject { ["configured"] = false, ["current"] = Program.Version, ["message"] = "还没有配置更新清单地址" };

            using (var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) })
            using (var request = new HttpRequestMessage(HttpMethod.Get, source))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", "HalfMcLauncher/" + Program.Version);
                using (var response = await client.SendAsync(request, ct).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                        return new JObject { ["configured"] = true, ["ok"] = false, ["message"] = "更新源返回 HTTP " + (int)response.StatusCode };
                    var json = JObject.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                    var latest = (json.Value<string>("tag_name") ?? json.Value<string>("version") ?? string.Empty).TrimStart('v', 'V');
                    var notes = json.Value<string>("body") ?? json.Value<string>("notes") ?? json.Value<string>("changelog") ?? string.Empty;
                    var link = json.Value<string>("html_url") ?? json.Value<string>("url") ?? string.Empty;
                    var release = json.Value<string>("published_at") ?? json.Value<string>("publishedAt") ?? string.Empty;
                    if (string.IsNullOrEmpty(latest))
                        return new JObject { ["configured"] = true, ["ok"] = false, ["message"] = "更新清单缺少 version 字段" };
                    return new JObject
                    {
                        ["configured"] = true,
                        ["ok"] = true,
                        ["current"] = Program.Version,
                        ["latest"] = latest,
                        ["hasUpdate"] = Compare(latest, Program.Version) > 0,
                        ["notes"] = notes,
                        ["link"] = link,
                        ["publishedAt"] = release
                    };
                }
            }
        }
    }
}
