using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace HalfMcLauncher
{
    /// <summary>
    /// 模组更新检查：用 SHA-1 向 Modrinth 查询每个 jar 的最新兼容版本。
    /// </summary>
    internal static class ModUpdates
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

        private static string GameVersion(JObject instance)
        {
            var value = instance.Value<string>("minecraft") ?? string.Empty;
            if (System.Text.RegularExpressions.Regex.IsMatch(value, @"^\d+\.\d+(\.\d+)?$")) return value;
            var name = instance.Value<string>("version") ?? string.Empty;
            var gameDir = instance.Value<string>("gameDir") ?? string.Empty;
            try
            {
                var path = Path.Combine(gameDir, name + ".json");
                if (File.Exists(path))
                {
                    var meta = JObject.Parse(File.ReadAllText(path));
                    var inherited = meta.Value<string>("inheritsFrom");
                    if (!string.IsNullOrEmpty(inherited)) return inherited;
                }
            }
            catch { }
            return string.Empty;
        }
        private static string Sha1(string file)
        {
            using (var sha = SHA1.Create())
            using (var stream = File.OpenRead(file))
                return string.Concat(sha.ComputeHash(stream).Select(b => b.ToString("x2")));
        }

        public static async Task<JArray> CheckAsync(JObject instance, CancellationToken ct)
        {
            var directory = Path.Combine(instance.Value<string>("gameDir"), "mods");
            var result = new JArray();
            if (!Directory.Exists(directory)) return result;
            var files = Directory.EnumerateFiles(directory)
                .Where(file => file.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                .Select(file => new { Path = file, Name = Path.GetFileName(file), Hash = Sha1(file) })
                .ToList();
            if (!files.Any()) return result;

            var loaders = new JArray();
            var loader = (instance.Value<string>("loader") ?? string.Empty).ToLowerInvariant();
            if (new[] { "fabric", "forge", "neoforge", "quilt" }.Contains(loader)) loaders.Add(loader);
            var game = GameVersion(instance);

            var latest = new Dictionary<string, JObject>(StringComparer.OrdinalIgnoreCase);
            foreach (var batch in files.Select((file, index) => new { file, index }).GroupBy(item => item.index / 80).Select(group => group.Select(item => item.file)))
            {
                ct.ThrowIfCancellationRequested();
                var payload = new JObject
                {
                    ["hashes"] = new JArray(batch.Select(file => file.Hash)),
                    ["algorithm"] = "sha1"
                };
                if (loaders.Count > 0) payload["loaders"] = loaders;
                if (!string.IsNullOrEmpty(game)) payload["game_versions"] = new JArray(game);
                using (var content = new StringContent(payload.ToString(), Encoding.UTF8, "application/json"))
                using (var response = await Http.PostAsync("https://api.modrinth.com/v2/version_files/update", content, ct))
                {
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException("Modrinth 更新查询失败：" + (int)response.StatusCode);
                    var map = JObject.Parse(await response.Content.ReadAsStringAsync());
                    foreach (var property in map.Properties())
                    {
                        if (property.Value is JObject version) latest[property.Name] = version;
                    }
                }
            }

            foreach (var file in files)
            {
                if (!latest.TryGetValue(file.Hash, out var version)) continue;
                var versionGames = version["game_versions"] as JArray;
                if (!string.IsNullOrEmpty(game) && versionGames != null && !versionGames.Any(item => string.Equals(item.ToString(), game, StringComparison.OrdinalIgnoreCase))) continue;
                var versions = version["files"] as JArray ?? new JArray();
                var binary = versions.FirstOrDefault(item => item.Value<bool?>("primary") == true) ?? versions.FirstOrDefault();
                if (binary == null) continue;
                var hashes = binary["hashes"] as JObject;
                var sameHash = versions.Any(item => string.Equals(item["hashes"]?.Value<string>("sha1"), file.Hash, StringComparison.OrdinalIgnoreCase));
                if (sameHash) continue;
                result.Add(new JObject
                {
                    ["file"] = file.Name,
                    ["hash"] = file.Hash,
                    ["projectId"] = version.Value<string>("project_id"),
                    ["versionId"] = version.Value<string>("id"),
                    ["version"] = version.Value<string>("version_number"),
                    ["name"] = version.Value<string>("name"),
                    ["filename"] = binary.Value<string>("filename"),
                    ["url"] = binary.Value<string>("url"),
                    ["sha1"] = hashes?.Value<string>("sha1") ?? string.Empty,
                    ["size"] = binary.Value<long?>("size") ?? 0
                });
            }
            return result;
        }

        public static async Task<JArray> ApplyAsync(JObject instance, JObject update, CancellationToken ct)
        {
            var directory = Path.Combine(instance.Value<string>("gameDir"), "mods");
            var oldFile = Mods.SafeFilename(update.Value<string>("file"));
            var newName = Mods.SafeFilename(update.Value<string>("filename"));
            var url = update.Value<string>("url");
            var expected = update.Value<string>("sha1") ?? string.Empty;
            if (string.IsNullOrEmpty(url)) throw new InvalidOperationException("更新地址为空");
            var target = Path.Combine(directory, newName);
            var disabled = oldFile.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
            var finalPath = disabled && !target.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) ? target + ".disabled" : target;
            var writePath = finalPath;
            byte[] data;
            using (var response = await Http.GetAsync(url, ct))
            {
                response.EnsureSuccessStatusCode();
                data = await response.Content.ReadAsByteArrayAsync();
            }
            if (!string.IsNullOrEmpty(expected))
            {
                using (var sha = SHA1.Create())
                {
                    var actual = string.Concat(sha.ComputeHash(data).Select(b => b.ToString("x2")));
                    if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("下载文件校验失败：" + newName);
                }
            }
            using (var stream = new FileStream(writePath, FileMode.Create, FileAccess.Write))
                await stream.WriteAsync(data, 0, data.Length);

            var oldPath = Path.Combine(directory, oldFile);
            try { File.Move(writePath, finalPath); }
            catch (Exception error)
            {
                try { File.Delete(writePath); } catch { }
                throw new InvalidOperationException("无法写入新版本，请确认 mods 目录可写：" + target, error);
            }
            if (File.Exists(oldPath) && !string.Equals(oldPath, finalPath, StringComparison.OrdinalIgnoreCase))
            {
                try { Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(oldPath, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin); }
                catch { try { File.Delete(oldPath); } catch { } }
            }
            return Mods.List(instance);
        }
    }
}
