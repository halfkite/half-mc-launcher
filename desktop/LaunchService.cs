using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;
using Newtonsoft.Json.Linq;

namespace HalfMcLauncher
{
    /// <summary>
    /// 用 CmlLib.Core 构造并启动游戏进程，把输出接到界面的日志与任务中心。
    /// 这一阶段只负责“启动已有实例”，下载/安装仍由 Electron 版或后续阶段处理。
    /// </summary>
    internal sealed class LaunchService
    {
        private static readonly Regex SecretPattern = new Regex(@"(Bearer\s+|--accessToken\s+|""access_token""\s*[:=]\s*""?)([^\s"",]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private readonly ConfigStore store;
        private readonly Action<string, object> emit;
        private readonly Func<JObject> snapshot;
        private readonly Dictionary<string, Process> running = new Dictionary<string, Process>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CancellationTokenSource> installs = new Dictionary<string, CancellationTokenSource>(StringComparer.Ordinal);
        private readonly object gate = new object();

        public LaunchService(ConfigStore store, Action<string, object> emit, Func<JObject> snapshot)
        {
            this.store = store;
            this.emit = emit;
            this.snapshot = snapshot;
        }

        public List<JObject> Tasks { get; } = new List<JObject>();
        public List<JObject> Logs { get; } = new List<JObject>();

        /// <summary>线程安全的快照副本，避免后台安装与界面读取互相踩踏。</summary>
        public JArray TasksJson()
        {
            lock (gate) return new JArray(Tasks.Select(item => item.DeepClone()));
        }

        public JArray LogsJson()
        {
            lock (gate) return new JArray(Logs.Select(item => item.DeepClone()));
        }

        public bool IsRunning(string id)
        {
            return !string.IsNullOrEmpty(id) && running.ContainsKey(id);
        }

        public void Log(string text, string level = "info", string instanceId = null)
        {
            var redacted = SecretPattern.Replace(text ?? string.Empty, "$1[REDACTED]");
            var entry = new JObject
            {
                ["time"] = DateTime.UtcNow.ToString("o"),
                ["level"] = level,
                ["text"] = redacted,
                ["instanceId"] = instanceId
            };
            lock (gate)
            {
                Logs.Add(entry);
                if (Logs.Count > 1500) Logs.RemoveRange(0, Logs.Count - 1500);
            }
            emit("log", entry);
        }

        public async Task<JToken> LaunchAsync(JObject instance)
        {
            var id = instance.Value<string>("id");
            var name = instance.Value<string>("name") ?? "实例";
            if (IsRunning(id)) throw new InvalidOperationException("此实例已在运行");

            var settings = store.Settings;
            var account = SelectedAccount();
            if (account == null) throw new InvalidOperationException("请先添加账户（微软账户或离线账户）");

            var root = instance.Value<string>("root");
            var version = instance.Value<string>("version");
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(version) || !Directory.Exists(root))
                throw new InvalidOperationException("实例目录不可用，请重新扫描");

            var memoryMode = instance.Value<string>("memoryMode") ?? "inherit";
            var memory = memoryMode == "inherit"
                ? settings.Value<int?>("memory") ?? 4096
                : instance.Value<int?>("memory") ?? 4096;
            if (memoryMode == "auto") memory = AutoMemory(instance.Value<string>("loader") != "Vanilla");

            var java = ResolveJava(instance, settings, root, version);
            var task = AddTask("启动 " + name, id);

            try
            {
                UpdateTask(task, "running", 15, "解析版本并构建启动参数");
                var path = new MinecraftPath(root);
                path.CreateDirs();
                var parameters = MinecraftLauncherParameters.CreateDefault(path);
                // 只用本地版本描述，避免启动时联网取版本清单，也避免写入游戏目录。
                parameters.VersionLoader = new CmlLib.Core.VersionLoader.LocalJsonVersionLoader(path);
                var launcher = new MinecraftLauncher(parameters);
                var accessToken = "0";
                if (account.Value<string>("kind") != "offline")
                {
                    UpdateTask(task, "running", 20, "验证微软账户");
                    accessToken = await MicrosoftAuth.SessionAsync(account.Value<string>("id"), CancellationToken.None);
                }
                var option = new MLaunchOption
                {
                    Session = new MSession(account.Value<string>("name"), accessToken, account.Value<string>("uuid")),
                    JavaPath = java,
                    MaximumRamMb = memory,
                    MinimumRamMb = Math.Min(512, memory),
                    GameLauncherName = "Half Mc Launcher",
                    GameLauncherVersion = Program.Version,
                    ScreenWidth = 1280,
                    ScreenHeight = 720,
                    FullScreen = false
                };

                var process = await launcher.BuildProcessAsync(version, option, CancellationToken.None);
                if (process == null) throw new InvalidOperationException("无法构建启动参数，请检查实例版本文件");

                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.StandardOutputEncoding = System.Text.Encoding.UTF8;
                process.StartInfo.StandardErrorEncoding = System.Text.Encoding.UTF8;
                process.EnableRaisingEvents = true;
                process.OutputDataReceived += (sender, args) => { if (args.Data != null) Log(args.Data, "info", id); };
                process.ErrorDataReceived += (sender, args) => { if (args.Data != null) Log(args.Data, "error", id); };
                process.Exited += (sender, args) => OnExited(id, name, process, task);

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                running[id] = process;

                UpdateTask(task, "running", 100, "游戏进程已启动（PID " + process.Id + "）");
                Log("启动 " + name + " · Java " + java + " · " + memory + " MB", "info", id);
                emit("snapshot", snapshot());
                return new JObject { ["pid"] = process.Id, ["java"] = java, ["memory"] = memory };
            }
            catch (Exception error)
            {
                UpdateTask(task, "error", 100, error.Message);
                Log("启动失败：" + error.Message, "error", id);
                throw;
            }
        }

        public bool Stop(string id)
        {
            Process process;
            if (string.IsNullOrEmpty(id) || !running.TryGetValue(id, out process)) throw new InvalidOperationException("游戏尚未运行");
            try
            {
                var kill = Process.Start(new ProcessStartInfo("taskkill", "/PID " + process.Id + " /T /F")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                kill?.WaitForExit(5000);
            }
            catch
            {
                try { process.Kill(); } catch { }
            }
            return true;
        }

        private void OnExited(string id, string name, Process process, JObject task)
        {
            running.Remove(id);
            var code = -1;
            try { code = process.ExitCode; } catch { }
            UpdateTask(task, code == 0 ? "success" : "error", 100, "游戏已退出，退出码 " + code);
            Log("游戏已退出，退出码 " + code + (code == 0 ? "" : "。请查看前面的错误日志以定位原因。"), code == 0 ? "info" : "error", id);
            emit("snapshot", null);
        }

        private JObject SelectedAccount()
        {
            var accounts = store.State["accounts"] as JArray;
            if (accounts == null || accounts.Count == 0) return null;
            var selected = store.State.Value<string>("selectedAccount");
            return accounts.FirstOrDefault(item => item.Value<string>("id") == selected) as JObject ?? accounts[0] as JObject;
        }

        private string ResolveJava(JObject instance, JObject settings, string root, string version)
        {
            var configured = instance.Value<string>("java");
            if (string.IsNullOrWhiteSpace(configured)) configured = settings.Value<string>("java");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;

            var extra = new List<string> { instance.Value<string>("java"), settings.Value<string>("java") }.Where(item => !string.IsNullOrWhiteSpace(item));
            var folders = (settings["javaRoots"] as JArray ?? new JArray()).Select(item => item.ToString()).ToList();
            var installed = JavaDetector.Discover(extra, folders);
            if (installed.Count == 0) throw new InvalidOperationException("未找到可用的 Java，请先在工具箱中检测或手动指定 java.exe");

            var required = RequiredJavaMajor(root, version);
            var chosen = installed.FirstOrDefault(item => item.Value<int>("major") == required)
                         ?? installed.Where(item => item.Value<int>("major") >= required).OrderBy(item => item.Value<int>("major")).FirstOrDefault()
                         ?? installed[0];
            return chosen.Value<string>("path");
        }

        private static int RequiredJavaMajor(string root, string version)
        {
            try
            {
                var file = Path.Combine(root, "versions", version, version + ".json");
                if (!File.Exists(file)) return 21;
                var json = JObject.Parse(File.ReadAllText(file));
                var major = json["javaVersion"]?.Value<int?>("majorVersion");
                if (major.HasValue) return major.Value;
                var name = json.Value<string>("inheritsFrom") ?? json.Value<string>("id");
                if (!string.IsNullOrEmpty(name) && name != version)
                {
                    var parent = Path.Combine(root, "versions", name, name + ".json");
                    if (File.Exists(parent))
                    {
                        var parentMajor = JObject.Parse(File.ReadAllText(parent))["javaVersion"]?.Value<int?>("majorVersion");
                        if (parentMajor.HasValue) return parentMajor.Value;
                    }
                }
            }
            catch { }
            return 21;
        }

        private static int AutoMemory(bool modded)
        {
            var total = SystemMemory.TotalMegabytes();
            var free = SystemMemory.AvailableMegabytes();
            if (total <= 0) return 4096;
            var reserve = Math.Max(1024, (int)Math.Round(total * 0.1));
            var ceiling = (int)Math.Floor(Math.Min(total * 0.6, free - reserve) / 256.0) * 256;
            if (ceiling < 512) throw new InvalidOperationException("当前可用内存不足，请先关闭其他程序后重试");
            var desired = modded ? 4096 : 2048;
            return Math.Max(512, Math.Min(desired, ceiling));
        }

        private JObject AddTask(string title, string instanceId)
        {
            var task = new JObject
            {
                ["id"] = Guid.NewGuid().ToString(),
                ["title"] = title,
                ["status"] = "running",
                ["progress"] = 0,
                ["detail"] = "准备中",
                ["createdAt"] = DateTime.UtcNow.ToString("o"),
                ["instanceId"] = instanceId
            };
            lock (gate)
            {
                Tasks.Add(task);
                if (Tasks.Count > 200) Tasks.RemoveRange(0, Tasks.Count - 200);
            }
            emit("tasks", TasksJson());
            return task;
        }

        private void UpdateTask(JObject task, string status, int progress, string detail)
        {
            task["status"] = status;
            task["progress"] = progress;
            task["detail"] = detail;
            emit("tasks", TasksJson());
        }

        /// <summary>通用后台任务：带取消、进度文案与错误处理。</summary>
        public string StartTask(string title, string instanceId, string root, Func<Action<string>, CancellationToken, Task> work, string successDetail = "完成")
        {
            var task = AddTask(title, instanceId);
            var taskId = task.Value<string>("id");
            var cts = new CancellationTokenSource();
            installs[taskId] = cts;
            Task.Run(async () =>
            {
                try
                {
                    UpdateTask(task, "running", 5, "准备中");
                    await work(text =>
                    {
                        Log(text, "info", instanceId);
                        UpdateTask(task, "running", task.Value<int?>("progress") ?? 5, text);
                    }, cts.Token);
                    UpdateTask(task, "success", 100, successDetail);
                    Log(title + "：" + successDetail, "info", instanceId);
                    emit("snapshot", snapshot());
                }
                catch (OperationCanceledException)
                {
                    UpdateTask(task, "cancelled", 100, "已取消");
                    Log(title + "已取消", "error", instanceId);
                }
                catch (Exception error)
                {
                    UpdateTask(task, "error", 100, error.Message);
                    Log(title + "失败：" + error.Message, "error", instanceId);
                }
                finally
                {
                    installs.Remove(taskId);
                    cts.Dispose();
                }
            });
            return taskId;
        }

        /// <summary>下载原版、安装加载器，可再叠加 OptiFine，返回任务 ID。</summary>
        public string StartInstall(string version, string loader, string loaderVersion, string root, string javaPath, string optifine = "")
        {
            var name = "安装 " + version + (string.IsNullOrEmpty(loader) || loader == "vanilla" ? "" : " · " + loader)
                       + (string.IsNullOrEmpty(optifine) ? "" : " + OptiFine");
            return StartTask(name, null, root, async (progress, ct) =>
            {
                progress("下载原版 " + version);
                var path = new MinecraftPath(root);
                path.CreateDirs();
                var launcher = new MinecraftLauncher(MinecraftLauncherParameters.CreateDefault(path));
                await launcher.InstallAsync(version, ct);
                if (!string.IsNullOrEmpty(loader) && loader != "vanilla")
                    await Loaders.InstallAsync(version, loader, loaderVersion, root, javaPath, ct, progress);
                if (!string.IsNullOrEmpty(optifine))
                    await Loaders.InstallOptiFineAsync(version, optifine, root, javaPath, ct, progress);
            }, "安装完成");
        }

        /// <summary>安装下载页选中的资源（含必要依赖）到实例。</summary>
        public string StartResourceInstall(JObject selection, JObject instance, ResourceService resources)
        {
            var name = "安装资源到 " + (instance.Value<string>("name") ?? "实例");
            return StartTask(name, instance.Value<string>("id"), instance.Value<string>("gameDir"), async (progress, ct) =>
            {
                var count = await resources.InstallAsync(selection, instance, progress, ct);
                progress("已安装 " + count + " 个文件（含必要依赖）");
            }, "资源安装完成");
        }

        public bool CancelTask(string id)
        {
            CancellationTokenSource cts;
            if (string.IsNullOrEmpty(id) || !installs.TryGetValue(id, out cts)) return false;
            cts.Cancel();
            return true;
        }
    }
}
