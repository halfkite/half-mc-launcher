using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace HalfMcLauncher
{
    /// <summary>
    /// electron/resources.ts 的 C# 移植：Modrinth / CurseForge 搜索、版本、依赖解析与安装。
    /// 下载写入实例的 mods / resourcepacks / shaderpacks，落盘前做大小与哈希校验。
    /// </summary>
    internal sealed class ResourceService
    {
        private const string UserAgent = "HalfMcLauncher/0.1.0";
        private const int MaxPages = 20;
        private const long MaxFileSize = 512L * 1024 * 1024;
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        private static readonly Dictionary<string, int> CurseClasses = new Dictionary<string, int> { ["mod"] = 6, ["resourcepack"] = 12, ["shader"] = 6552 };
        private static readonly Dictionary<string, int> CurseLoaders = new Dictionary<string, int> { ["forge"] = 1, ["fabric"] = 4, ["quilt"] = 5, ["neoforge"] = 6 };

        private readonly string directory;
        private string curseKey;

        public ResourceService(string dataDirectory)
        {
            directory = dataDirectory;
            LoadKey();
        }

        public bool Configured() { return !string.IsNullOrEmpty(curseKey); }

        public bool Configure(string key)
        {
            var file = Path.Combine(directory, "curseforge.bin");
            if (string.IsNullOrWhiteSpace(key))
            {
                if (File.Exists(file)) File.Delete(file);
                curseKey = null;
                return false;
            }
            var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(key.Trim()), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(file + ".tmp", protectedBytes);
            if (File.Exists(file)) File.Delete(file);
            File.Move(file + ".tmp", file);
            curseKey = key.Trim();
            return true;
        }

        private void LoadKey()
        {
            try
            {
                var file = Path.Combine(directory, "curseforge.bin");
                if (File.Exists(file))
                    curseKey = Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(file), null, DataProtectionScope.CurrentUser));
            }
            catch { }
        }

        // ---------- 搜索 ----------

        public async Task<JObject> SearchAsync(JObject query, CancellationToken ct)
        {
            var source = query.Value<string>("source") ?? "modrinth";
            var kind = query.Value<string>("kind") ?? "mod";
            var text = query.Value<string>("query") ?? string.Empty;
            var game = query.Value<string>("game") ?? string.Empty;
            var loader = (query.Value<string>("loader") ?? string.Empty).ToLowerInvariant();
            var offset = Math.Max(0, query.Value<int?>("offset") ?? 0);

            if (source == "modrinth")
            {
                var facets = new JArray { new JArray("project_type:" + kind) };
                if (!string.IsNullOrEmpty(game)) facets.Add(new JArray("versions:" + game));
                if (!string.IsNullOrEmpty(loader) && kind == "mod") facets.Add(new JArray("categories:" + loader));
                var url = "https://api.modrinth.com/v2/search?" + string.Join("&", new[]
                {
                    "query=" + Uri.EscapeDataString(text),
                    "facets=" + Uri.EscapeDataString(facets.ToString(Newtonsoft.Json.Formatting.None)),
                    "limit=20",
                    "offset=" + offset,
                    "index=" + (string.IsNullOrEmpty(text) ? "downloads" : "relevance")
                });
                var result = await GetJson(url, ct);
                var projects = new JArray();
                foreach (var hit in result["hits"] as JArray ?? new JArray())
                    projects.Add(new JObject
                    {
                        ["id"] = hit.Value<string>("project_id"),
                        ["source"] = "modrinth",
                        ["name"] = hit.Value<string>("title"),
                        ["summary"] = hit.Value<string>("description"),
                        ["downloads"] = hit.Value<long?>("downloads") ?? 0,
                        ["author"] = hit.Value<string>("author"),
                        ["icon"] = hit.Value<string>("icon_url") ?? string.Empty,
                        ["versions"] = hit["versions"] as JArray ?? new JArray()
                    });
                return new JObject { ["total"] = result.Value<int?>("total_hits") ?? projects.Count, ["projects"] = projects };
            }

            RequireKey();
            var parameters = new List<string>
            {
                "gameId=432", "classId=" + CurseClasses[kind], "searchFilter=" + Uri.EscapeDataString(text),
                "pageSize=20", "index=" + offset, "sortField=6", "sortOrder=desc"
            };
            if (!string.IsNullOrEmpty(game)) parameters.Add("gameVersion=" + Uri.EscapeDataString(game));
            if (!string.IsNullOrEmpty(loader) && kind == "mod" && CurseLoaders.ContainsKey(loader)) parameters.Add("modLoaderType=" + CurseLoaders[loader]);
            var curse = await GetJson("https://api.curseforge.com/v1/mods/search?" + string.Join("&", parameters), ct);
            var list = new JArray();
            foreach (var entry in curse["data"] as JArray ?? new JArray())
                list.Add(new JObject
                {
                    ["id"] = entry.Value<string>("id"),
                    ["source"] = "curseforge",
                    ["name"] = entry.Value<string>("name"),
                    ["summary"] = entry.Value<string>("summary"),
                    ["downloads"] = entry.Value<long?>("downloadCount") ?? 0,
                    ["author"] = string.Join(", ", (entry["authors"] as JArray ?? new JArray()).Select(item => item.Value<string>("name"))),
                    ["icon"] = entry["logo"]?.Value<string>("thumbnailUrl") ?? entry["logo"]?.Value<string>("url") ?? string.Empty,
                    ["versions"] = new JArray((entry["latestFilesIndexes"] as JArray ?? new JArray()).Select(item => item.Value<string>("gameVersion")).Where(item => !string.IsNullOrEmpty(item)).Distinct())
                });
            return new JObject { ["total"] = curse["pagination"]?.Value<int?>("totalCount") ?? list.Count, ["projects"] = list };
        }

        // ---------- 版本 ----------

        private sealed class Version
        {
            public string Id, ProjectId, Source, Name, Filename, Url, Hash, Algorithm, Kind;
            public long Size;
            public List<string> Games = new List<string>();
            public List<string> Loaders = new List<string>();
            public List<JObject> Dependencies = new List<JObject>();

            public JObject ToJson()
            {
                return new JObject
                {
                    ["id"] = Id, ["projectId"] = ProjectId, ["source"] = Source, ["name"] = Name,
                    ["filename"] = Filename, ["url"] = Url, ["hash"] = Hash, ["algorithm"] = Algorithm,
                    ["size"] = Size, ["games"] = new JArray(Games), ["loaders"] = new JArray(Loaders),
                    ["kind"] = Kind, ["dependencies"] = new JArray(Dependencies.Select(item => item.DeepClone()))
                };
            }
        }

        public async Task<JArray> VersionsAsync(JObject selection, JObject instance, CancellationToken ct)
        {
            var list = await FetchVersions(selection, instance, ct);
            list = list.Where(item => instance == null || Compatible(item, instance)).ToList();
            return new JArray(list.Select(item => item.ToJson()));
        }

        private async Task<List<Version>> FetchVersions(JObject selection, JObject instance, CancellationToken ct)
        {
            var source = selection.Value<string>("source") ?? "modrinth";
            var kind = selection.Value<string>("kind") ?? "mod";
            var projectId = selection.Value<string>("projectId");
            if (string.IsNullOrEmpty(projectId)) throw new InvalidOperationException("缺少资源项目");
            var versions = new List<Version>();

            if (source == "modrinth")
            {
                var query = new List<string> { "include_changelog=false" };
                if (instance != null)
                {
                    query.Add("game_versions=" + Uri.EscapeDataString("[\"" + instance.Value<string>("minecraft") + "\"]"));
                    if (kind == "mod") query.Add("loaders=" + Uri.EscapeDataString("[\"" + (instance.Value<string>("loader") ?? "vanilla").ToLowerInvariant() + "\"]"));
                }
                var array = await GetJson("https://api.modrinth.com/v2/project/" + Uri.EscapeDataString(projectId) + "/version?" + string.Join("&", query), ct);
                foreach (var item in array as JArray ?? new JArray()) versions.Add(MapModrinthVersion(item, kind));
                return versions;
            }

            RequireKey();
            var parameters = new List<string> { "pageSize=50" };
            if (instance != null)
            {
                parameters.Add("gameVersion=" + Uri.EscapeDataString(instance.Value<string>("minecraft")));
                var loader = (instance.Value<string>("loader") ?? string.Empty).ToLowerInvariant();
                if (kind == "mod" && CurseLoaders.ContainsKey(loader)) parameters.Add("modLoaderType=" + CurseLoaders[loader]);
            }
            var response = await GetJson("https://api.curseforge.com/v1/mods/" + Uri.EscapeDataString(projectId) + "/files?" + string.Join("&", parameters), ct);
            foreach (var item in response["data"] as JArray ?? new JArray()) versions.Add(MapCurseVersion(item, kind));
            return versions;
        }

        private static Version MapModrinthVersion(JToken item, string kind)
        {
            var files = item["files"] as JArray ?? new JArray();
            var file = files.FirstOrDefault(entry => entry.Value<bool?>("primary") == true) ?? files.FirstOrDefault();
            if (file == null) throw new InvalidOperationException("此版本没有可下载文件");
            var hashes = file["hashes"] as JObject;
            var algorithm = hashes?["sha512"] != null ? "sha512" : "sha1";
            var version = new Version
            {
                Id = item.Value<string>("id"), ProjectId = item.Value<string>("project_id"), Source = "modrinth",
                Name = item.Value<string>("name"), Filename = file.Value<string>("filename"), Url = file.Value<string>("url"),
                Hash = hashes?[algorithm]?.ToString() ?? string.Empty, Algorithm = algorithm,
                Size = file.Value<long?>("size") ?? 0, Kind = kind
            };
            version.Games = (item["game_versions"] as JArray ?? new JArray()).Select(value => value.ToString()).ToList();
            version.Loaders = (item["loaders"] as JArray ?? new JArray()).Select(value => value.ToString()).ToList();
            foreach (var dependency in item["dependencies"] as JArray ?? new JArray())
                if (dependency.Value<string>("dependency_type") == "required")
                    version.Dependencies.Add(new JObject { ["projectId"] = dependency.Value<string>("project_id"), ["versionId"] = dependency.Value<string>("version_id") });
            return version;
        }

        private static Version MapCurseVersion(JToken item, string kind)
        {
            var hash = (item["hashes"] as JArray ?? new JArray()).FirstOrDefault(entry => entry.Value<int?>("algo") == 1)
                       ?? (item["hashes"] as JArray ?? new JArray()).FirstOrDefault(entry => entry.Value<int?>("algo") == 2);
            var version = new Version
            {
                Id = item.Value<string>("id"), ProjectId = item.Value<string>("modId"), Source = "curseforge",
                Name = item.Value<string>("displayName"), Filename = item.Value<string>("fileName"),
                Url = item.Value<string>("downloadUrl") ?? string.Empty,
                Hash = hash?.Value<string>("value") ?? string.Empty,
                Algorithm = hash?.Value<int?>("algo") == 2 ? "md5" : "sha1",
                Size = item.Value<long?>("fileLength") ?? 0, Kind = kind
            };
            version.Games = (item["gameVersions"] as JArray ?? new JArray()).Select(value => value.ToString()).ToList();
            version.Loaders = version.Games.Select(value => value.ToLowerInvariant())
                .Where(value => CurseLoaders.ContainsKey(value)).ToList();
            foreach (var dependency in item["dependencies"] as JArray ?? new JArray())
                if (dependency.Value<int?>("relationType") == 3)
                    version.Dependencies.Add(new JObject { ["projectId"] = dependency.Value<string>("modId") });
            return version;
        }

        private async Task<Version> FetchVersion(JObject selection, CancellationToken ct)
        {
            var source = selection.Value<string>("source") ?? "modrinth";
            var versionId = selection.Value<string>("versionId");
            if (string.IsNullOrEmpty(versionId)) throw new InvalidOperationException("请选择资源版本");
            if (source == "modrinth")
            {
                var item = await GetJson("https://api.modrinth.com/v2/version/" + Uri.EscapeDataString(versionId), ct);
                return MapModrinthVersion(item, selection.Value<string>("kind") ?? "mod");
            }
            RequireKey();
            var projectId = selection.Value<string>("projectId") ?? string.Empty;
            var response = await GetJson("https://api.curseforge.com/v1/mods/" + Uri.EscapeDataString(projectId) + "/files/" + Uri.EscapeDataString(versionId), ct);
            return MapCurseVersion(response["data"], selection.Value<string>("kind") ?? "mod");
        }

        private static bool Compatible(Version version, JObject instance)
        {
            var minecraft = instance.Value<string>("minecraft");
            var loader = (instance.Value<string>("loader") ?? "vanilla").ToLowerInvariant();
            if (!string.IsNullOrEmpty(minecraft) && version.Games.Count > 0 && !version.Games.Contains(minecraft)) return false;
            if (version.Kind == "mod" && version.Loaders.Count > 0 && !version.Loaders.Contains(loader)) return false;
            return true;
        }

        // ---------- 依赖计划 ----------

        public async Task<JArray> PlanAsync(JObject selection, JObject instance, CancellationToken ct)
        {
            var plan = await BuildPlan(selection, instance, ct);
            return new JArray(plan.Select(item => item.ToJson()));
        }

        private async Task<List<Version>> BuildPlan(JObject selection, JObject instance, CancellationToken ct)
        {
            var plan = new List<Version>();
            var resolved = new Dictionary<string, string>();
            var visiting = new HashSet<string>();

            async Task Walk(Version version, int depth)
            {
                if (depth > 16 || plan.Count >= 100) throw new InvalidOperationException("资源依赖数量或深度超过限制");
                if (!Compatible(version, instance)) throw new InvalidOperationException("资源 " + version.Name + " 与实例的游戏版本或加载器不兼容");
                if (resolved.ContainsKey(version.ProjectId))
                {
                    if (resolved[version.ProjectId] != version.Id) throw new InvalidOperationException("资源依赖要求了同一项目的不同版本");
                    return;
                }
                if (visiting.Contains(version.ProjectId)) throw new InvalidOperationException("资源依赖出现循环，已停止安装");

                Mods.SafeFilename(version.Filename);
                if (!Regex.IsMatch(version.Filename, version.Kind == "mod" ? "\\.jar$" : "\\.zip$", RegexOptions.IgnoreCase))
                    throw new InvalidOperationException("资源文件类型不匹配");
                if (string.IsNullOrEmpty(version.Hash) || !Regex.IsMatch(version.Hash, "^[a-f0-9]+$", RegexOptions.IgnoreCase))
                    throw new InvalidOperationException("资源缺少有效校验信息");
                if (!version.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("资源下载必须使用 HTTPS");
                if (version.Size <= 0 || version.Size > MaxFileSize) throw new InvalidOperationException("单个资源文件需小于 512 MiB");

                visiting.Add(version.ProjectId);
                foreach (var dependency in version.Dependencies)
                {
                    var projectId = dependency.Value<string>("projectId");
                    var versionId = dependency.Value<string>("versionId");
                    if (string.IsNullOrEmpty(projectId) && string.IsNullOrEmpty(versionId)) throw new InvalidOperationException("存在需手动安装的外部依赖");
                    Version resolvedDependency;
                    if (!string.IsNullOrEmpty(versionId) && version.Source == "modrinth")
                    {
                        resolvedDependency = await FetchVersion(new JObject { ["source"] = version.Source, ["kind"] = "mod", ["projectId"] = projectId ?? "unknown", ["versionId"] = versionId }, ct);
                    }
                    else
                    {
                        var list = await FetchVersions(new JObject { ["source"] = version.Source, ["kind"] = "mod", ["projectId"] = projectId }, instance, ct);
                        resolvedDependency = list.FirstOrDefault(item => Compatible(item, instance));
                        if (resolvedDependency == null) throw new InvalidOperationException("依赖 " + projectId + " 没有兼容版本");
                    }
                    await Walk(resolvedDependency, depth + 1);
                }
                visiting.Remove(version.ProjectId);
                resolved[version.ProjectId] = version.Id;
                plan.Add(version);
            }

            var root = await FetchVersion(selection, ct);
            if (root.ProjectId != selection.Value<string>("projectId")) throw new InvalidOperationException("资源项目和版本不匹配");
            await Walk(root, 0);
            return plan;
        }

        // ---------- 安装 ----------

        public async Task<int> InstallAsync(JObject selection, JObject instance, Action<string> progress, CancellationToken ct)
        {
            var plan = await BuildPlan(selection, instance, ct);
            var staging = new List<(string Temp, string Target, Version Version)>();
            var added = new List<string>();
            try
            {
                var threads = Math.Max(1, Math.Min(8, DownloadSettings.Threads));
                var bag = new System.Collections.Concurrent.ConcurrentBag<(string Temp, string Target, Version Version)>();
                using (var semaphore = new SemaphoreSlim(threads))
                {
                    var tasks = plan.Select(async (version, index) =>
                    {
                        ct.ThrowIfCancellationRequested();
                        var folder = version.Kind == "mod" ? "mods" : version.Kind == "shader" ? "shaderpacks" : "resourcepacks";
                        var directory = Mods.ResourceDirectory(instance, folder);
                        var target = Path.Combine(directory, version.Filename);
                        if (File.Exists(target))
                        {
                            if (string.Equals(HashFile(target, version.Algorithm), version.Hash, StringComparison.OrdinalIgnoreCase)) return;
                            throw new InvalidOperationException("已存在不同的同名文件：" + version.Filename + "。请先在 Mod 管理中处理");
                        }
                        if (File.Exists(target + ".disabled")) throw new InvalidOperationException("此模组已被禁用：" + version.Filename);

                        var temp = Path.Combine(directory, ".half-" + Guid.NewGuid().ToString("N") + ".part");
                        bag.Add((temp, target, version));
                        progress?.Invoke("下载 " + version.Filename + "（" + (index + 1) + "/" + plan.Count + "）");
                        await semaphore.WaitAsync(ct);
                        try { await DownloadAsync(version, temp, ct); }
                        catch { try { if (File.Exists(temp)) File.Delete(temp); } catch { } throw; }
                        finally { semaphore.Release(); }
                    }).ToList();
                    await Task.WhenAll(tasks);
                }
                staging.AddRange(bag);

                var conflicts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in Directory.EnumerateFiles(Path.Combine(instance.Value<string>("gameDir"), "mods"), "*.jar"))
                {
                    try { foreach (var id in Mods.Metadata(file).Ids) conflicts[id] = Path.GetFileName(file); } catch { }
                }
                foreach (var item in staging.Where(entry => entry.Version.Kind == "mod"))
                {
                    foreach (var id in Mods.Metadata(item.Temp).Ids)
                    {
                        if (conflicts.TryGetValue(id, out var owner) && owner != item.Version.Filename)
                            throw new InvalidOperationException("模组 " + id + " 已由 " + owner + " 提供，请先处理旧版本");
                        conflicts[id] = item.Version.Filename;
                    }
                }

                progress?.Invoke("写入已校验资源");
                foreach (var item in staging)
                {
                    ct.ThrowIfCancellationRequested();
                    File.Copy(item.Temp, item.Target, false);
                    added.Add(item.Target);
                }
                return plan.Count;
            }
            catch
            {
                foreach (var file in added) { try { File.Delete(file); } catch { } }
                throw;
            }
            finally
            {
                foreach (var item in staging) { try { if (File.Exists(item.Temp)) File.Delete(item.Temp); } catch { } }
            }
        }

        private async Task DownloadAsync(Version version, string target, CancellationToken ct)
        {
            var attempts = DownloadSettings.Retries + 1;
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        timeout.CancelAfter(TimeSpan.FromSeconds(DownloadSettings.TimeoutSeconds));
                        await DownloadOnceAsync(version, target, timeout.Token);
                    }
                    return;
                }
                catch (Exception) when (attempt < attempts && !ct.IsCancellationRequested)
                {
                    try { if (File.Exists(target)) File.Delete(target); } catch { }
                    await Task.Delay(250 * attempt, ct);
                }
            }
        }

        private async Task DownloadOnceAsync(Version version, string target, CancellationToken ct)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, version.Url))
            using (var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException("下载失败（HTTP " + (int)response.StatusCode + "）");
                using (var input = await response.Content.ReadAsStreamAsync())
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var hash = CreateHash(version.Algorithm))
                {
                    var buffer = new byte[81920];
                    long total = 0;
                    int read;
                    while ((read = await input.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                    {
                        total += read;
                        if (total > version.Size || total > MaxFileSize) throw new InvalidOperationException("下载文件超出声明大小");
                        hash.TransformBlock(buffer, 0, read, null, 0);
                        await output.WriteAsync(buffer, 0, read, ct);
                    }
                    hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    var digest = string.Concat(hash.Hash.Select(value => value.ToString("x2")));
                    if (total != version.Size || !string.Equals(digest, version.Hash, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("文件完整性校验失败：" + version.Filename);
                }
            }
        }

        private static string HashFile(string file, string algorithm)
        {
            using (var hash = CreateHash(algorithm))
            using (var stream = File.OpenRead(file))
            {
                var digest = hash.ComputeHash(stream);
                return string.Concat(digest.Select(value => value.ToString("x2")));
            }
        }

        private static HashAlgorithm CreateHash(string algorithm)
        {
            switch ((algorithm ?? "sha1").ToLowerInvariant())
            {
                case "sha512": return SHA512.Create();
                case "md5": return MD5.Create();
                default: return SHA1.Create();
            }
        }

        // ---------- 其它 ----------

        public async Task<string> ProjectUrlAsync(JObject selection, CancellationToken ct)
        {
            var source = selection.Value<string>("source") ?? "modrinth";
            var kind = selection.Value<string>("kind") ?? "mod";
            var projectId = selection.Value<string>("projectId");
            if (source == "modrinth") return "https://modrinth.com/" + kind + "/" + Uri.EscapeDataString(projectId);
            RequireKey();
            var project = await GetJson("https://api.curseforge.com/v1/mods/" + Uri.EscapeDataString(projectId), ct);
            var url = new Uri(project["data"]?["links"]?.Value<string>("websiteUrl") ?? string.Empty);
            if (url.Scheme != "https" || !(url.Host == "www.curseforge.com" || url.Host == "curseforge.com"))
                throw new InvalidOperationException("项目页面地址无效");
            return url.ToString();
        }

        private void RequireKey()
        {
            if (string.IsNullOrEmpty(curseKey))
                throw new InvalidOperationException("请先在设置中配置 Half 自己的 CurseForge API 密钥，或切换到 Modrinth");
        }

        private async Task<JToken> GetJson(string url, CancellationToken ct)
        {
            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                if (url.Contains("curseforge.com")) request.Headers.TryAddWithoutValidation("x-api-key", curseKey ?? string.Empty);
                using (var response = await Http.SendAsync(request, ct))
                {
                    var text = await response.Content.ReadAsStringAsync();
                    if (!response.IsSuccessStatusCode)
                        throw new InvalidOperationException("请求失败（HTTP " + (int)response.StatusCode + "）：" + text.Trim().Substring(0, Math.Min(300, text.Trim().Length)));
                    return JToken.Parse(text);
                }
            }
        }
    }
}
