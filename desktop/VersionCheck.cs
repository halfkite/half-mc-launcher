using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CmlLib.Core;
using Newtonsoft.Json.Linq;

namespace HalfMcLauncher
{
    /// <summary>
    /// 版本文件校验与修复：检查版本 JSON、客户端 jar、libraries 与 assets 索引/对象，
    /// 修复时重装继承的原版版本，让 CmlLib 补齐缺失文件。
    /// </summary>
    internal static class VersionCheck
    {
        private static string Root(JObject instance) => InstanceScanner.canonical(instance.Value<string>("root"));
        private static string VersionName(JObject instance) => instance.Value<string>("version") ?? string.Empty;

        private static string VersionJson(string root, string id) => Path.Combine(root, "versions", id, id + ".json");

        private static bool AppliesToWindows(JToken library)
        {
            var rules = library["rules"] as JArray;
            if (rules == null || rules.Count == 0) return true;
            var allow = false;
            foreach (var rule in rules)
            {
                var os = rule["os"]?.Value<string>("name");
                if (!string.IsNullOrEmpty(os) && !string.Equals(os, "windows", StringComparison.OrdinalIgnoreCase)) continue;
                var action = rule.Value<string>("action");
                if (string.Equals(action, "allow", StringComparison.OrdinalIgnoreCase)) allow = true;
                else if (string.Equals(action, "disallow", StringComparison.OrdinalIgnoreCase)) return false;
            }
            return allow;
        }
        private static JObject LoadJson(string root, string id)
        {
            var path = VersionJson(root, id);
            if (!File.Exists(path)) return null;
            try { return JObject.Parse(File.ReadAllText(path)); } catch { return null; }
        }

        private static string BaseId(string root, string id)
        {
            var meta = LoadJson(root, id);
            var inherited = meta?.Value<string>("inheritsFrom");
            return string.IsNullOrEmpty(inherited) ? id : inherited;
        }

        public static JObject Verify(JObject instance)
        {
            var root = Root(instance);
            var id = VersionName(instance);
            var missing = new JArray();
            var checkedFiles = 0;

            void Missing(string kind, string name, string detail)
            {
                missing.Add(new JObject { ["kind"] = kind, ["name"] = name, ["detail"] = detail });
            }

            var meta = LoadJson(root, id);
            if (meta == null) Missing("version", id + ".json", "版本描述缺失或无法解析");
            checkedFiles++;

            var baseId = string.IsNullOrEmpty(meta?.Value<string>("inheritsFrom")) ? id : meta.Value<string>("inheritsFrom");
            var baseMeta = baseId == id ? meta : LoadJson(root, baseId);
            if (baseId != id)
            {
                checkedFiles++;
                if (baseMeta == null) Missing("version", baseId + ".json", "继承的原版版本描述缺失");
            }

            var jar = Path.Combine(root, "versions", baseId, baseId + ".jar");
            checkedFiles++;
            if (!File.Exists(jar)) Missing("client", baseId + ".jar", "客户端 jar 缺失");
            else if (new FileInfo(jar).Length == 0) Missing("client", baseId + ".jar", "客户端 jar 为空文件");

            var libraries = baseMeta?["libraries"] as JArray ?? new JArray();
            checkedFiles += libraries.Count;
            foreach (var library in libraries)
            {
                if (!AppliesToWindows(library)) continue;
                var artifact = library["downloads"]?["artifact"];
                var path = artifact?.Value<string>("path");
                if (string.IsNullOrEmpty(path)) continue;
                var file = Path.Combine(root, "libraries", path.Replace('/', '\\'));
                if (!File.Exists(file)) { Missing("library", path, "依赖库缺失"); continue; }
                var size = artifact.Value<long?>("size") ?? 0;
                if (size > 0 && new FileInfo(file).Length != size) Missing("library", path, "依赖库大小不符");
            }

            var assetIndex = baseMeta?["assetIndex"] ?? baseMeta?["assets"];
            var assetId = assetIndex?["id"]?.ToString() ?? meta?["assetIndex"]?["id"]?.ToString();
            var assetJson = string.IsNullOrEmpty(assetId) ? null : Path.Combine(root, "assets", "indexes", assetId + ".json");
            checkedFiles++;
            if (!string.IsNullOrEmpty(assetId))
            {
                if (assetJson == null || !File.Exists(assetJson)) Missing("assets", assetId + ".json", "资源索引缺失");
                else
                {
                    try
                    {
                        var index = JObject.Parse(File.ReadAllText(assetJson));
                        var objects = index["objects"] as JObject;
                        var scan = 0;
                        foreach (var property in objects?.Properties() ?? Enumerable.Empty<JProperty>())
                        {
                            if (scan++ >= 2000) break;
                            var hash = property.Value.Value<string>("hash") ?? string.Empty;
                            if (hash.Length < 2) continue;
                            var target = Path.Combine(root, "assets", "objects", hash.Substring(0, 2), hash);
                            if (File.Exists(target)) continue;
                            Missing("asset", property.Name, "资源对象缺失（抽样 2000 个）");
                            if (missing.Count > 120) break;
                        }
                        checkedFiles += Math.Min(objects?.Count ?? 0, 2000);
                    }
                    catch { Missing("assets", assetId + ".json", "资源索引无法解析"); }
                }
            }

            var versionDir = Path.Combine(root, "versions", id);
            if (!Directory.Exists(versionDir)) Missing("version", id, "实例目录缺失");

            return new JObject
            {
                ["ok"] = missing.Count == 0,
                ["version"] = id,
                ["base"] = baseId,
                ["checked"] = checkedFiles,
                ["missing"] = missing
            };
        }

        public static async Task<JObject> RepairAsync(JObject instance, Action<string> progress, CancellationToken ct)
        {
            var root = Root(instance);
            var id = VersionName(instance);
            var baseId = BaseId(root, id);
            if (string.IsNullOrEmpty(baseId)) throw new InvalidOperationException("找不到要修复的版本");
            progress?.Invoke("重新安装 " + baseId + " 的文件");
            var path = new MinecraftPath(root);
            path.CreateDirs();
            var launcher = new MinecraftLauncher(MinecraftLauncherParameters.CreateDefault(path));
            await launcher.InstallAsync(baseId, ct);
            progress?.Invoke("校验修复结果");
            return Verify(instance);
        }
    }
}
