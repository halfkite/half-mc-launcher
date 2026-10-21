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

            var javaExe = Environment.GetEnvironmentVariable("HALF_TEST_JAVA");
            if (!string.IsNullOrWhiteSpace(javaExe) && File.Exists(javaExe))
            {
                var probe = JavaDetector.Probe(javaExe).GetAwaiter().GetResult();
                Check(probe.Value<int>("major") >= 17, "Java 主版本识别（" + probe.Value<string>("version") + "）");
                Check(!string.IsNullOrEmpty(probe.Value<string>("arch")), "Java 架构读取正常：" + probe.Value<string>("arch"));
                var found = JavaDetector.Discover(new[] { javaExe }, Array.Empty<string>());
                Check(found.Any(item => string.Equals(item.Value<string>("path"), javaExe, StringComparison.OrdinalIgnoreCase)), "Discover 能返回指定的 Java");
            }
            else
            {
                Console.WriteLine("SKIP 未设置 HALF_TEST_JAVA，跳过 Java 检测");
            }

            // 用一个合成的最小版本描述验证 CmlLib 的启动参数构建，不触碰真实游戏目录。
            try
            {
                var fakeRoot = Path.Combine(temp, "mc");
                var fakeVersion = "smoke-1.0";
                var fakeDir = Path.Combine(fakeRoot, "versions", fakeVersion);
                Directory.CreateDirectory(fakeDir);
                Directory.CreateDirectory(Path.Combine(fakeRoot, "assets", "indexes"));
                File.WriteAllText(Path.Combine(fakeRoot, "assets", "indexes", "legacy.json"),
                    "{\"objects\":{}}", new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(fakeDir, fakeVersion + ".json"),
                    "{\"id\":\"smoke-1.0\",\"type\":\"release\",\"mainClass\":\"net.minecraft.client.main.Main\",\"assets\":\"legacy\"," +
                    "\"assetIndex\":{\"id\":\"legacy\",\"url\":\"\",\"sha1\":\"\",\"size\":0},\"libraries\":[]," +
                    "\"arguments\":{\"game\":[\"--username\",\"${auth_player_name}\",\"--version\",\"${version_name}\",\"--gameDir\",\"${game_directory}\"]," +
                    "\"jvm\":[\"-Djava.library.path=${natives_directory}\",\"-cp\",\"${classpath}\"]}}",
                    new UTF8Encoding(false));

                var fakePath = new CmlLib.Core.MinecraftPath(fakeRoot);
                var parameters = CmlLib.Core.MinecraftLauncherParameters.CreateDefault(fakePath);
                parameters.VersionLoader = new CmlLib.Core.VersionLoader.LocalJsonVersionLoader(fakePath);
                var fakeLauncher = new CmlLib.Core.MinecraftLauncher(parameters);
                var option = new CmlLib.Core.ProcessBuilder.MLaunchOption
                {
                    Session = new CmlLib.Core.Auth.MSession("SmokeTest", "0", "00000000000000000000000000000000"),
                    JavaPath = javaExe,
                    MaximumRamMb = 2048,
                    GameLauncherName = "Half Mc Launcher",
                    GameLauncherVersion = "0.1.0"
                };
                var built = fakeLauncher.BuildProcessAsync(fakeVersion, option, System.Threading.CancellationToken.None).AsTask().GetAwaiter().GetResult();
                var arguments = built.StartInfo.Arguments;
                Check(arguments.Contains("--username SmokeTest"), "启动参数包含用户名");
                Check(arguments.Contains("--version smoke-1.0"), "启动参数包含版本名");
                Check(arguments.Contains("-Xmx2048M") || arguments.Contains("-Xmx2048m"), "启动参数包含内存上限");
                Check(!string.IsNullOrWhiteSpace(built.StartInfo.FileName), "启动程序路径已解析：" + built.StartInfo.FileName);
            }
            catch (Exception error)
            {
                Check(false, "CmlLib 构建启动参数失败：" + error.Message);
            }

            Directory.Delete(temp, true);
            Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILURE(S)");
            return failures == 0 ? 0 : 1;
        }
    }
}
