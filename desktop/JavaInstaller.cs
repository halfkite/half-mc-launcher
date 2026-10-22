using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.Zip;
using Newtonsoft.Json.Linq;

namespace HalfMcLauncher
{
    /// <summary>
    /// 从 Adoptium 下载 Temurin JRE 并解压到数据目录，随后登记为 Java 搜索目录。
    /// </summary>
    internal static class JavaInstaller
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

        private static async Task<JObject> LatestAsync(int feature, CancellationToken ct)
        {
            var url = "https://api.adoptium.net/v3/assets/latest/" + feature +
                      "/hotspot?architecture=x64&image_type=jre&os=windows&vendor=eclipse";
            using (var response = await Http.GetAsync(url, ct))
            {
                response.EnsureSuccessStatusCode();
                var text = await response.Content.ReadAsStringAsync();
                var assets = JArray.Parse(text);
                var asset = assets.FirstOrDefault();
                var package = asset?["binary"]?["package"];
                var link = package?.Value<string>("link");
                if (string.IsNullOrEmpty(link)) throw new InvalidOperationException("没有找到可用的 Java " + feature + " 安装包");
                return new JObject
                {
                    ["feature"] = feature,
                    ["name"] = package.Value<string>("name"),
                    ["link"] = link,
                    ["size"] = package.Value<long?>("size") ?? 0,
                    ["version"] = asset["version"]?.Value<string>("semver") ?? asset.Value<string>("release_name")
                };
            }
        }

        public static async Task<JObject> InstallAsync(int feature, Action<string, int> progress, CancellationToken ct)
        {
            progress?.Invoke("获取 Java " + feature + " 信息", 0);
            var asset = await LatestAsync(feature, ct);
            var baseDir = Path.Combine(AppPaths.DataDir, "java");
            Directory.CreateDirectory(baseDir);
            var archive = Path.Combine(baseDir, asset.Value<string>("name") ?? ("temurin-" + feature + ".zip"));
            var total = asset.Value<long?>("size") ?? 0;
            progress?.Invoke("下载 " + asset.Value<string>("name"), 2);

            using (var response = await Http.GetAsync(asset.Value<string>("link"), HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                var length = response.Content.Headers.ContentLength ?? total;
                using (var input = await response.Content.ReadAsStreamAsync())
                using (var output = new FileStream(archive, FileMode.Create, FileAccess.Write))
                {
                    var buffer = new byte[81920];
                    var read = 0L;
                    while (true)
                    {
                        var count = await input.ReadAsync(buffer, 0, buffer.Length, ct);
                        if (count <= 0) break;
                        await output.WriteAsync(buffer, 0, count, ct);
                        read += count;
                        if (length > 0) progress?.Invoke("下载 Java " + feature, (int)Math.Min(90, read * 90 / length));
                    }
                }
            }

            var target = Path.Combine(baseDir, "temurin-" + feature);
            if (Directory.Exists(target))
            {
                try { Directory.Delete(target, true); } catch { }
            }
            progress?.Invoke("解压 Java " + feature, 92);
            new FastZip().ExtractZip(archive, target, null);
            try { File.Delete(archive); } catch { }

            var settings = Program.MainWindow.Store.Settings;
            var roots = settings["javaRoots"] as JArray ?? new JArray();
            var root = InstanceScanner.canonical(baseDir);
            if (!roots.Any(item => string.Equals(item.ToString(), root, StringComparison.OrdinalIgnoreCase)))
            {
                roots.Add(root);
                settings["javaRoots"] = roots;
                Program.MainWindow.Store.Save();
                Program.MainWindow.SendEvent("snapshot", Program.MainWindow.BuildSnapshot());
            }
            progress?.Invoke("Java " + feature + " 安装完成", 100);
            return new JObject { ["feature"] = feature, ["root"] = target, ["version"] = asset.Value<string>("version") };
        }
    }
}
