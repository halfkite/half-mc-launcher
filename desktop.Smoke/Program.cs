using System;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace HalfMcLauncher.Smoke
{
    internal static class Program
    {
        private static int failures;

        private static void Check(bool condition, string message)
        {
            Console.WriteLine((condition ? "PASS " : "FAIL ") + message);
            if (!condition) failures++;
        }

        private static int Main()
        {
            var temp = Path.Combine(Path.GetTempPath(), "half-smoke-" + Guid.NewGuid().ToString("N"));
            var gameDir = Path.Combine(temp, "game");
            Directory.CreateDirectory(gameDir);

            Environment.SetEnvironmentVariable("HALF_DATA_DIR", Path.Combine(temp, "data"));
            AppPaths.Initialize();

            var defaults = ConfigStore.DefaultState();
            Check(defaults["workspace"]?["layout"]?.ToString() == "single", "默认工作区为单栏");
            Check(defaults["workspace"]?["panes"]?.Count() == 4, "默认工作区包含四个分栏");
            Check(defaults["settings"]?["memory"]?.Value<int>() == 4096, "默认内存 4096 MB");

            var fabricDir = Path.Combine(gameDir, "versions", "1.21.1-Fabric");
            Directory.CreateDirectory(Path.Combine(fabricDir, "mods"));
            File.WriteAllText(Path.Combine(fabricDir, "1.21.1-Fabric.json"),
                "{\"id\":\"1.21.1-Fabric\",\"mainClass\":\"net.fabricmc.loader.impl.launch.knot.KnotClient\",\"inheritsFrom\":\"1.21.1\",\"libraries\":[{\"name\":\"net.fabricmc:fabric-loader:0.16.9\"}]}",
                new UTF8Encoding(false));

            var vanillaDir = Path.Combine(gameDir, "versions", "1.21.1");
            Directory.CreateDirectory(vanillaDir);
            File.WriteAllText(Path.Combine(vanillaDir, "1.21.1.json"),
                "{\"id\":\"1.21.1\",\"mainClass\":\"net.minecraft.client.main.Main\"}",
                new UTF8Encoding(false));

            var brokenDir = Path.Combine(gameDir, "versions", "broken");
            Directory.CreateDirectory(brokenDir);
            File.WriteAllText(Path.Combine(brokenDir, "broken.json"), "{ not json", new UTF8Encoding(false));

            var state = ConfigStore.DefaultState();
            state["sources"] = new JArray(new JObject { ["id"] = "s1", ["path"] = gameDir, ["label"] = "test" });
            var scan = InstanceScanner.Scan(state);
            Check(scan.Instances.Count == 3, "扫描出三个版本目录，得到 " + scan.Instances.Count);
            var fabric = scan.Instances.First(item => item.Value<string>("name") == "1.21.1-Fabric");
            Check(fabric.Value<string>("loader") == "Fabric", "识别 Fabric 加载器");
            Check(fabric.Value<string>("minecraft") == "1.21.1", "继承父版本 1.21.1");
            Check(fabric.Value<bool>("isolated"), "存在 mods 目录时默认独立游戏目录");
            Check((fabric.Value<string>("id") ?? string.Empty).Length == 24, "实例 ID 为 24 位十六进制");
            var broken = scan.Instances.First(item => item.Value<string>("name") == "broken");
            Check(!string.IsNullOrEmpty(broken.Value<string>("issue")), "损坏版本给出 issue 提示");

            var store = new ConfigStore();
            store.ReplaceRoot(state);
            store.Save();
            Check(File.Exists(AppPaths.ConfigFile), "config.json 已写入");
            var loaded = ConfigStore.Load();
            Check(loaded.State["sources"]?.Count() == 1, "重新加载后保留登记目录");
            Check(loaded.Warning == null, "正常配置不产生警告");

            var hidden = (JArray)state["hidden"];
            hidden.Add(fabric.Value<string>("id"));
            var rescan = InstanceScanner.Scan(state);
            Check(rescan.Instances.All(item => item.Value<string>("name") != "1.21.1-Fabric"), "隐藏的实例不再出现");

            Directory.Delete(temp, true);
            Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILURE(S)");
            return failures == 0 ? 0 : 1;
        }
    }
}
