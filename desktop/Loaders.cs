using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CmlLib.Core;
using CmlLib.Core.Installer.Forge;
using CmlLib.Core.Installer.Forge.Versions;
using CmlLib.Core.Installer.NeoForge;
using CmlLib.Core.ModLoaders.FabricMC;
using CmlLib.Core.ModLoaders.QuiltMC;
using Newtonsoft.Json.Linq;

namespace HalfMcLauncher
{
    /// <summary>
    /// 加载器版本列表与安装：Fabric / Quilt 走 CmlLib 内置安装器，Forge / NeoForge 调用官方安装器。
    /// OptiFine 与 LiteLoader 暂未接入，列表会明确为空。
    /// </summary>
    internal static class Loaders
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        public static async Task<JArray> ListAsync(string minecraft, string loader, CancellationToken ct)
        {
            var result = new JArray();
            if (string.IsNullOrWhiteSpace(minecraft)) return result;
            switch ((loader ?? "vanilla").ToLowerInvariant())
            {
                case "fabric":
                    {
                        var array = await GetJson("https://meta.fabricmc.net/v2/versions/loader/" + Uri.EscapeDataString(minecraft), ct);
                        foreach (var item in array as JArray ?? new JArray())
                        {
                            var version = item["loader"]?.Value<string>("version");
                            if (string.IsNullOrEmpty(version)) continue;
                            var stable = item["loader"]?.Value<bool?>("stable") ?? !System.Text.RegularExpressions.Regex.IsMatch(version, "alpha|beta|pre|rc", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            result.Add(Entry(version, stable, false, string.Empty, "官方"));
                        }
                        break;
                    }
                case "quilt":
                    {
                        var array = await GetJson("https://meta.quiltmc.org/v3/versions/loader/" + Uri.EscapeDataString(minecraft), ct);
                        foreach (var item in array as JArray ?? new JArray())
                        {
                            var version = item["loader"]?.Value<string>("version");
                            if (string.IsNullOrEmpty(version)) continue;
                            var stable = item["loader"]?.Value<bool?>("stable") ?? !System.Text.RegularExpressions.Regex.IsMatch(version, "alpha|beta|pre|rc", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                            result.Add(Entry(version, stable, false, string.Empty, "官方"));
                        }
                        break;
                    }
                case "forge":
                    {
                        var launcher = new MinecraftLauncher(MinecraftLauncherParameters.CreateDefault(new MinecraftPath()));
                        var installer = new ForgeInstaller(launcher, Http);
                        var versions = await installer.GetForgeVersions(minecraft);
                        foreach (var version in versions)
                        {
                            result.Add(Entry(version.ForgeVersionName, true, version.IsRecommendedVersion,
                                version.IsLatestVersion ? "最新" : string.Empty, "官方"));
                        }
                        break;
                    }
                case "neoforge":
                    {
                        var launcher = new MinecraftLauncher(MinecraftLauncherParameters.CreateDefault(new MinecraftPath()));
                        var installer = new NeoForgeInstaller(launcher, Http);
                        var versions = await installer.GetNeoForgeVersions(minecraft);
                        foreach (var version in versions)
                        {
                            result.Add(Entry(version.NeoForgeVersionName,
                                !System.Text.RegularExpressions.Regex.IsMatch(version.NeoForgeVersionName, "alpha|beta|pre|rc", System.Text.RegularExpressions.RegexOptions.IgnoreCase),
                                false, string.Empty, "官方"));
                        }
                        break;
                    }
                case "optifine":
                    {
                        var array = await GetJson("https://bmclapi2.bangbang93.com/optifine/" + Uri.EscapeDataString(minecraft), ct);
                        foreach (var item in array as JArray ?? new JArray())
                        {
                            var type = item.Value<string>("type");
                            var patch = item.Value<string>("patch");
                            if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(patch)) continue;
                            var version = type + "_" + patch;
                            result.Add(Entry(version, !patch.ToLowerInvariant().Contains("pre"), false,
                                string.IsNullOrEmpty(item.Value<string>("forge")) ? "独立安装" : "Forge " + item.Value<string>("forge"), "BMCLAPI"));
                        }
                        break;
                    }
                case "liteloader":
                    {
                        if (!System.Text.RegularExpressions.Regex.IsMatch(minecraft, "^1\\.(?:[5-9]|1[0-2])(?:\\.|$)")) break;
                        var response = await GetJson("https://bmclapi2.bangbang93.com/liteloader/list?mcversion=" + Uri.EscapeDataString(minecraft), ct);
                        var build = response["build"];
                        if (build == null) break;
                        var versionName = build.Value<string>("version");
                        if (string.IsNullOrEmpty(versionName)) break;
                        result.Add(Entry(versionName, response.Value<string>("type") != "SNAPSHOT", false,
                            "旧版组件 · " + response.Value<string>("type"), "BMCLAPI"));
                        break;
                    }
            }
            return result;
        }

        private static JObject Entry(string version, bool stable, bool recommended, string detail, string source)
        {
            return new JObject
            {
                ["version"] = version,
                ["stable"] = stable,
                ["recommended"] = recommended,
                ["detail"] = detail,
                ["source"] = source
            };
        }

        /// <summary>把加载器装进已存在的游戏目录，返回生成的版本名。</summary>
        public static async Task<string> InstallAsync(string minecraft, string loader, string loaderVersion, string root, string javaPath, CancellationToken ct, Action<string> progress)
        {
            var path = new MinecraftPath(root);
            path.CreateDirs();
            switch ((loader ?? "vanilla").ToLowerInvariant())
            {
                case "vanilla":
                    return minecraft;
                case "fabric":
                    {
                        progress?.Invoke("安装 Fabric " + loaderVersion);
                        var installer = new FabricInstaller(Http);
                        return await installer.Install(minecraft, loaderVersion, path);
                    }
                case "quilt":
                    {
                        progress?.Invoke("安装 Quilt " + loaderVersion);
                        var installer = new QuiltInstaller(Http);
                        return await installer.Install(minecraft, loaderVersion, path);
                    }
                case "forge":
                    {
                        progress?.Invoke("安装 Forge " + loaderVersion + "（需要 Java 运行安装器）");
                        var launcher = new MinecraftLauncher(MinecraftLauncherParameters.CreateDefault(path));
                        var installer = new ForgeInstaller(launcher, Http);
                        var options = new ForgeInstallOptions { JavaPath = javaPath, CancellationToken = ct, SkipIfAlreadyInstalled = true };
                        return await installer.Install(minecraft, loaderVersion, options);
                    }
                case "neoforge":
                    {
                        progress?.Invoke("安装 NeoForge " + loaderVersion + "（需要 Java 运行安装器）");
                        var launcher = new MinecraftLauncher(MinecraftLauncherParameters.CreateDefault(path));
                        var installer = new NeoForgeInstaller(launcher, Http);
                        var options = new ForgeInstallOptions { JavaPath = javaPath, CancellationToken = ct, SkipIfAlreadyInstalled = true };
                        return await installer.Install(minecraft, loaderVersion, options);
                    }
                default:
                    throw new NotSupportedException("暂不支持安装 " + loader + "，请选择原版、Fabric、Quilt、Forge 或 NeoForge");
            }
        }

        private static async Task<JToken> GetJson(string url, CancellationToken ct)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", "HalfMcLauncher/0.1.0");
                using (var response = await Http.SendAsync(request, ct))
                {
                    var text = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException("请求失败（HTTP " + (int)response.StatusCode + "）");
                    return JToken.Parse(text);
                }
            }
        }
    }
}
