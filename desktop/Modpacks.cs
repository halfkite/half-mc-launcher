using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using ICSharpCode.SharpZipLib.Zip;
using Newtonsoft.Json.Linq;

namespace HalfMcLauncher
{
    /// <summary>
    /// 整合包导入/导出：自定义 halfpack、Modrinth mrpack、CurseForge manifest 与普通压缩包。
    /// </summary>
    internal static class Modpacks
    {
        private static readonly string[] OverrideFolders = { "mods", "config", "defaultconfigs", "resourcepacks", "shaderpacks", "kubejs", "scripts" };
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        public static JObject Export(JObject instance, string target)
        {
            var gameDir = InstanceScanner.canonical(instance.Value<string>("gameDir"));
            if (!Directory.Exists(gameDir)) throw new InvalidOperationException("实例目录不存在");
            var manifest = new JObject
            {
                ["format"] = "halfpack-1",
                ["name"] = instance.Value<string>("name") ?? "整合包",
                ["minecraft"] = instance.Value<string>("minecraft") ?? string.Empty,
                ["loader"] = instance.Value<string>("loader") ?? "vanilla",
                ["loaderVersion"] = instance.Value<string>("loaderVersion") ?? string.Empty,
                ["exportedAt"] = DateTime.Now.ToString("o")
            };
            var count = 0;
            using (var stream = File.Create(target))
            using (var zip = new ZipOutputStream(stream))
            {
                zip.SetLevel(6);
                AddText(zip, "halfpack.json", manifest.ToString());
                foreach (var folder in OverrideFolders)
                {
                    var directory = Path.Combine(gameDir, folder);
                    if (!Directory.Exists(directory)) continue;
                    foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                    {
                        var relative = file.Substring(gameDir.Length).TrimStart('\\', '/').Replace('\\', '/');
                        AddFile(zip, file, "overrides/" + relative);
                        count++;
                    }
                }
                var options = Path.Combine(gameDir, "options.txt");
                if (File.Exists(options)) { AddFile(zip, options, "overrides/options.txt"); count++; }
            }
            return new JObject
            {
                ["manifest"] = manifest,
                ["files"] = count,
                ["path"] = target
            };
        }

        private static void AddText(ZipOutputStream zip, string name, string text)
        {
            var data = Encoding.UTF8.GetBytes(text);
            zip.PutNextEntry(new ZipEntry(name) { Size = data.Length, DateTime = DateTime.Now });
            zip.Write(data, 0, data.Length);
            zip.CloseEntry();
        }

        private static void AddFile(ZipOutputStream zip, string file, string name)
        {
            var info = new FileInfo(file);
            zip.PutNextEntry(new ZipEntry(name) { Size = info.Length, DateTime = info.LastWriteTime });
            using (var input = File.OpenRead(file))
                input.CopyTo(zip);
            zip.CloseEntry();
        }

        private static string SafeRelative(string name)
        {
            var normalized = name.Replace('\\', '/').TrimStart('/');
            if (normalized.Contains("..") || Path.IsPathRooted(normalized)) throw new InvalidOperationException("整合包内包含非法路径");
            return normalized;
        }

        public static async Task<JObject> ImportAsync(JObject instance, string source, Action<string> progress, System.Threading.CancellationToken ct)
        {
            if (!File.Exists(source)) throw new InvalidOperationException("整合包不存在");
            var gameDir = InstanceScanner.canonical(instance.Value<string>("gameDir"));
            var warnings = new JArray();
            var extracted = 0;
            var downloaded = 0;
            JObject manifest = null;
            var mode = "generic";

            using (var zip = new ZipFile(source))
            {
                var half = zip.GetEntry("halfpack.json");
                var modrinth = zip.GetEntry("modrinth.index.json");
                var curse = zip.GetEntry("manifest.json");
                if (half != null)
                {
                    manifest = JObject.Parse(ReadEntry(zip, half));
                    mode = "halfpack";
                }
                else if (modrinth != null)
                {
                    manifest = JObject.Parse(ReadEntry(zip, modrinth));
                    mode = "modrinth";
                }
                else if (curse != null)
                {
                    manifest = JObject.Parse(ReadEntry(zip, curse));
                    mode = "curseforge";
                }

                if (manifest != null)
                {
                    var minecraft = manifest.Value<string>("minecraft") ?? manifest["dependencies"]?.Value<string>("minecraft");
                    var targetMinecraft = instance.Value<string>("minecraft");
                    if (!string.IsNullOrEmpty(minecraft) && !string.IsNullOrEmpty(targetMinecraft) && minecraft != targetMinecraft)
                        warnings.Add("整合包面向 " + minecraft + "，当前实例是 " + targetMinecraft + "，启动可能失败。");
                    var loader = mode == "curseforge" ? (manifest["minecraft"]?["modLoaders"] as JArray)?.FirstOrDefault()?.Value<string>("id") : manifest["dependencies"]?.Value<string>("fabric-loader");
                    if (mode == "modrinth" && loader == null) loader = manifest["dependencies"]?.Value<string>("quilt-loader");
                    if (!string.IsNullOrEmpty(loader)) warnings.Add("整合包加载器：" + loader);
                }

                if (mode == "modrinth")
                {
                    foreach (var file in manifest["files"] as JArray ?? new JArray())
                    {
                        ct.ThrowIfCancellationRequested();
                        var path = SafeRelative(file.Value<string>("path") ?? string.Empty);
                        if (path.Length == 0 || !path.StartsWith("mods/", StringComparison.OrdinalIgnoreCase)) { warnings.Add("跳过非 mods 文件：" + path); continue; }
                        var target = Path.Combine(gameDir, path.Replace('/', '\\'));
                        if (File.Exists(target)) continue;
                        var url = (file["downloads"] as JArray)?.FirstOrDefault()?.ToString();
                        if (string.IsNullOrEmpty(url)) { warnings.Add("没有下载地址：" + path); continue; }
                        progress?.Invoke("下载 " + Path.GetFileName(path));
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        var data = await Http.GetByteArrayAsync(url);
                        var hash = (file["hashes"] as JObject)?.Value<string>("sha1");
                        if (!string.IsNullOrEmpty(hash) && !string.Equals(Sha1(data), hash, StringComparison.OrdinalIgnoreCase))
                        {
                            warnings.Add("校验失败，已跳过：" + Path.GetFileName(path));
                            continue;
                        }
                        await WriteBytes(target, data);
                        downloaded++;
                    }
                }

                if (mode == "curseforge")
                {
                    var files = manifest["files"] as JArray ?? new JArray();
                    if (files.Count > 0) warnings.Add("CurseForge 整合包有 " + files.Count + " 个文件需要通过 CurseForge API 下载；未配置密钥时只会解压 overrides。");
                }

                foreach (ZipEntry entry in zip)
                {
                    ct.ThrowIfCancellationRequested();
                    if (!entry.IsFile) continue;
                    var name = entry.Name.Replace('\\', '/');
                    string relative = null;
                    if (name.StartsWith("overrides/", StringComparison.OrdinalIgnoreCase)) relative = name.Substring("overrides/".Length);
                    else if (mode == "generic" && !name.Contains("..") && !name.StartsWith("versions/", StringComparison.OrdinalIgnoreCase)
                             && !name.StartsWith("libraries/", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)
                             && !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || (mode == "generic" && relative != null))
                        relative = name;
                    if (string.IsNullOrEmpty(relative) || relative.EndsWith("/")) continue;
                    relative = SafeRelative(relative);
                    if (relative.StartsWith("versions/", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("libraries/", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("assets/", StringComparison.OrdinalIgnoreCase)) continue;
                    var target = Path.Combine(gameDir, relative.Replace('/', '\\'));
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var input = zip.GetInputStream(entry))
                    using (var output = File.Create(target))
                        input.CopyTo(output);
                    extracted++;
                }
            }

            return new JObject
            {
                ["mode"] = mode,
                ["extracted"] = extracted,
                ["downloaded"] = downloaded,
                ["warnings"] = warnings
            };
        }

        private static string ReadEntry(ZipFile zip, ZipEntry entry)
        {
            using (var input = zip.GetInputStream(entry))
            using (var reader = new StreamReader(input, Encoding.UTF8))
                return reader.ReadToEnd();
        }

        private static async Task WriteBytes(string path, byte[] data)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
                await stream.WriteAsync(data, 0, data.Length);
        }

        private static string Sha1(byte[] data)
        {
            using (var sha = SHA1.Create())
                return string.Concat(sha.ComputeHash(data).Select(b => b.ToString("x2")));
        }
    }
}
