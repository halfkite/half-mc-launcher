using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CmlLib.Core;

namespace HalfMcLauncher
{
    /// <summary>
    /// 下载入口：按设置把 Mojang 官方地址改写到 BMCLAPI 镜像。
    /// 自动模式优先官方，失败（网络异常或 5xx）时回退镜像。
    /// </summary>
    internal static class Network
    {
        public const string OfficialManifest = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
        public const string MirrorManifest = "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json";

        private static string Source => DownloadSettings.Source;
        public static bool MirrorEnabled => string.Equals(Source, "mirror", StringComparison.OrdinalIgnoreCase);
        public static string ManifestUrl => MirrorEnabled ? MirrorManifest : OfficialManifest;

        public static HttpClient CreateClient()
        {
            var timeout = TimeSpan.FromSeconds(Math.Max(30, DownloadSettings.TimeoutSeconds));
            if (string.Equals(Source, "official", StringComparison.OrdinalIgnoreCase))
                return new HttpClient { Timeout = timeout };
            return new HttpClient(new MirrorHandler(string.Equals(Source, "auto", StringComparison.OrdinalIgnoreCase))
            {
                InnerHandler = new HttpClientHandler()
            })
            {
                Timeout = timeout
            };
        }

        public static MinecraftLauncherParameters Parameters(MinecraftPath path)
        {
            var parameters = MinecraftLauncherParameters.CreateDefault(path);
            parameters.HttpClient = CreateClient();
            return parameters;
        }

        public static MinecraftLauncher Create(MinecraftPath path) => new MinecraftLauncher(Parameters(path));

        private sealed class MirrorHandler : DelegatingHandler
        {
            private readonly bool auto;
            public MirrorHandler(bool auto) { this.auto = auto; }

            private static Uri Rewrite(Uri uri)
            {
                if (uri == null || uri.Scheme != Uri.UriSchemeHttps) return uri;
                var host = uri.Host.ToLowerInvariant();
                var path = uri.AbsolutePath;
                string target;
                switch (host)
                {
                    case "launchermeta.mojang.com":
                    case "piston-meta.mojang.com":
                    case "piston-data.mojang.com":
                    case "launcher.mojang.com":
                        target = path;
                        break;
                    case "libraries.minecraft.net":
                        target = "/maven" + path;
                        break;
                    case "resources.download.minecraft.net":
                        target = "/assets" + path;
                        break;
                    default:
                        return uri;
                }
                return new UriBuilder(uri)
                {
                    Scheme = Uri.UriSchemeHttps,
                    Host = "bmclapi2.bangbang93.com",
                    Port = -1,
                    Path = target
                }.Uri;
            }

            private static HttpRequestMessage Clone(HttpRequestMessage request, Uri uri)
            {
                var clone = new HttpRequestMessage(request.Method, uri);
                foreach (var header in request.Headers) clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
                if (request.Content != null)
                {
                    clone.Content = request.Content;
                    foreach (var header in request.Content.Headers) clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
                return clone;
            }

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                if (!auto)
                {
                    request.RequestUri = Rewrite(request.RequestUri);
                    return await base.SendAsync(request, cancellationToken);
                }
                try
                {
                    var response = await base.SendAsync(request, cancellationToken);
                    if ((int)response.StatusCode < 500) return response;
                    response.Dispose();
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // 官方地址不可用时尝试镜像。
                }
                var retry = Clone(request, Rewrite(request.RequestUri));
                return await base.SendAsync(retry, cancellationToken);
            }
        }
    }
}
