using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ICSharpCode.SharpZipLib.Zip;
using Newtonsoft.Json.Linq;

namespace HalfMcLauncher
{
    /// <summary>
    /// 拖拽导入：把 jar 放进 mods，把资源包/光影 zip 放进对应目录。
    /// 整合包体积大且需要选择版本，仍走内容页的导入流程。
    /// </summary>
    internal static class DropImport
    {
        private static JObject Find(string id)
        {
            var snapshot = Program.MainWindow?.BuildSnapshot();
            return (snapshot?["instances"] as JArray)?.OfType<JObject>()
                .FirstOrDefault(item => item.Value<string>("id") == id);
        }

        private static string KindOf(string archive)
        {
            try
            {
                using (var zip = new ICSharpCode.SharpZipLib.Zip.ZipFile(File.OpenRead(archive)))
                {
                    var hasMeta = zip.GetEntry("pack.mcmeta") != null;
                    var hasShaders = zip.Cast<ZipEntry>().Any(entry => entry.Name.StartsWith("shaders/", StringComparison.OrdinalIgnoreCase));
                    if (hasShaders && !hasMeta) return "shaderpacks";
                    if (hasMeta) return hasShaders ? "shaderpacks" : "resourcepacks";
                    return null;
                }
            }
            catch { return null; }
        }

        public static JObject Handle(string instanceId, IEnumerable<string> paths)
        {
            var instance = Find(instanceId);
            if (instance == null) return new JObject { ["ok"] = false, ["message"] = "先在实例页面选择一个实例，再拖入文件" };
            if (instance.Value<bool>("protected")) return new JObject { ["ok"] = false, ["message"] = "该实例已保护，不能导入内容" };

            var gameDir = InstanceScanner.canonical(instance.Value<string>("gameDir"));
            var jars = new List<string>();
            var packs = new List<(string path, string kind)>();
            var skipped = new JArray();
            foreach (var path in paths.Where(File.Exists))
            {
                if (path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) { jars.Add(path); continue; }
                if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    var kind = KindOf(path);
                    if (kind == null) { skipped.Add(new JObject { ["file"] = Path.GetFileName(path), ["reason"] = "不是资源包或光影，整合包请用导入按钮" }); continue; }
                    packs.Add((path, kind));
                    continue;
                }
                skipped.Add(new JObject { ["file"] = Path.GetFileName(path), ["reason"] = "不支持的文件类型" });
            }

            var imported = new JArray();
            if (jars.Count > 0)
            {
                try
                {
                    Mods.Import(instance, jars);
                    foreach (var jar in jars) imported.Add(new JObject { ["file"] = Path.GetFileName(jar), ["kind"] = "mods" });
                }
                catch (Exception error) { skipped.Add(new JObject { ["file"] = "模组", ["reason"] = error.Message }); }
            }
            foreach (var pack in packs)
            {
                try
                {
                    var folder = Path.Combine(gameDir, pack.kind);
                    Directory.CreateDirectory(folder);
                    var destination = Path.Combine(folder, Path.GetFileName(pack.path));
                    if (File.Exists(destination)) throw new InvalidOperationException("已存在同名文件");
                    File.Copy(pack.path, destination);
                    imported.Add(new JObject { ["file"] = Path.GetFileName(pack.path), ["kind"] = pack.kind });
                }
                catch (Exception error)
                {
                    skipped.Add(new JObject { ["file"] = Path.GetFileName(pack.path), ["reason"] = error.Message });
                }
            }

            var result = new JObject
            {
                ["ok"] = imported.Count > 0,
                ["imported"] = imported,
                ["skipped"] = skipped,
                ["instance"] = instance.Value<string>("name")
            };
            result["message"] = imported.Count > 0
                ? "已导入 " + imported.Count + " 个文件到 " + instance.Value<string>("name")
                : "没有可导入的文件";
            if (skipped.Count > 0) result["message"] += "，跳过 " + skipped.Count + " 个";
            return result;
        }
    }
}
