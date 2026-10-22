using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;

namespace HalfMcLauncher
{
    /// <summary>
    /// 外壳对外暴露的 IPC。已接入的方法覆盖实例浏览、设置、账户、Java、启动、下载与模组管理；
    /// 未接入的方法返回空数据或明确提示，避免界面在未实现的页面上崩溃。
    /// </summary>
    internal static class HostApi
    {
        private static MainForm Form { get { return Program.MainWindow; } }
        private static ConfigStore Store { get { return Program.MainWindow.Store; } }
        private static ResourceService resources;

        private static ResourceService Resources
        {
            get { return resources ?? (resources = new ResourceService(AppPaths.DataDir)); }
        }

        internal static async Task<JToken> HandleAsync(string method, JToken payload)
        {
            switch (method)
            {
                case "snapshot": return Form.BuildSnapshot();

                case "workspace.save":
                    Store.State["workspace"] = payload;
                    Store.Save();
                    return new JValue(true);

                case "preset.save":
                    {
                        var name = payload?.Value<string>("name")?.Trim() ?? string.Empty;
                        if (name.Length == 0) throw new InvalidOperationException("预设名称不能为空");
                        var presets = Store.State["presets"] as JArray ?? new JArray();
                        if (presets.Count >= 30) throw new InvalidOperationException("最多保存 30 个预设");
                        presets.Add(new JObject { ["id"] = Guid.NewGuid().ToString(), ["name"] = name, ["workspace"] = payload["workspace"]?.DeepClone() });
                        Store.State["presets"] = presets;
                        Store.Save();
                        Form.SendEvent("snapshot", Form.BuildSnapshot());
                        return new JValue(true);
                    }

                case "preset.delete":
                    {
                        var presets = Store.State["presets"] as JArray ?? new JArray();
                        var target = presets.FirstOrDefault(item => item.Value<string>("id") == payload?.ToString());
                        if (target != null) presets.Remove(target);
                        Store.Save();
                        Form.SendEvent("snapshot", Form.BuildSnapshot());
                        return new JValue(true);
                    }

                case "source.add":
                    {
                        var picked = FolderPicker.Pick(Form.Handle, "选择 Minecraft 目录、versions 文件夹，或包含多个游戏目录的容器", AppPaths.BaseDir);
                        if (string.IsNullOrWhiteSpace(picked)) return JValue.CreateNull();
                        var directory = InstanceScanner.canonical(picked);
                        var sources = Store.State["sources"] as JArray ?? new JArray();
                        if (sources.Any(item => string.Equals(item.Value<string>("path"), directory, StringComparison.OrdinalIgnoreCase)))
                            throw new InvalidOperationException("此目录已经添加");
                        // 目录扫描失败不应阻断登记：把提示留到 snapshot 的 scanWarnings 里。
                        try { InstanceScanner.FindRoots(directory); } catch { }
                        sources.Add(new JObject
                        {
                            ["id"] = Guid.NewGuid().ToString(),
                            ["path"] = directory,
                            ["label"] = Path.GetFileName(directory).Length > 0 ? Path.GetFileName(directory) : directory
                        });
                        Store.State["sources"] = sources;
                        Store.Save();
                        Form.Rescan();
                        return Form.BuildSnapshot();
                    }

                case "source.remove":
                    {
                        var sources = Store.State["sources"] as JArray ?? new JArray();
                        var target = sources.FirstOrDefault(item => item.Value<string>("id") == payload?.ToString());
                        if (target != null) sources.Remove(target);
                        Store.Save();
                        Form.Rescan();
                        return Form.BuildSnapshot();
                    }

                case "scan":
                    Form.Rescan();
                    return Form.BuildSnapshot();

                case "instance.save":
                    {
                        var id = payload?.Value<string>("id");
                        var options = payload?["options"]?.DeepClone();
                        if (string.IsNullOrEmpty(id) || options == null) throw new InvalidOperationException("实例参数无效");
                        var map = Store.State["options"] as JObject ?? new JObject();
                        map[id] = options;
                        Store.State["options"] = map;
                        Store.Save();
                        Form.Rescan();
                        return Form.BuildSnapshot();
                    }

                case "instance.remove":
                    {
                        var hidden = Store.State["hidden"] as JArray ?? new JArray();
                        var id = payload?.ToString();
                        if (!string.IsNullOrEmpty(id) && !hidden.Any(item => item.ToString() == id)) hidden.Add(id);
                        Store.State["hidden"] = hidden;
                        Store.Save();
                        Form.Rescan();
                        return Form.BuildSnapshot();
                    }

                case "instance.open":
                    {
                        var instance = Form.Rescan().Instances.FirstOrDefault(item => item.Value<string>("id") == payload?.ToString());
                        if (instance == null) throw new InvalidOperationException("此实例已不存在，请重新扫描");
                        Process.Start(new ProcessStartInfo("explorer.exe", "\"" + instance.Value<string>("gameDir") + "\"") { UseShellExecute = true });
                        return new JValue(true);
                    }

                case "settings.save":
                    Store.State["settings"] = payload;
                    Store.Save();
                    Form.SendEvent("snapshot", Form.BuildSnapshot());
                    return new JValue(true);

                case "appearance.save":
                    {
                        var settings = Store.State["settings"] as JObject ?? new JObject();
                        foreach (var key in new[] { "theme", "accent", "reduceMotion", "fontScale", "opacity", "glass" })
                            if (payload?[key] != null) settings[key] = payload[key].DeepClone();
                        Store.State["settings"] = settings;
                        Store.Save();
                        Form.SendEvent("snapshot", Form.BuildSnapshot());
                        return new JValue(true);
                    }

                case "account.offline":
                    {
                        var name = payload?.ToString()?.Trim() ?? string.Empty;
                        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9_]{3,16}$"))
                            throw new InvalidOperationException("离线账户名称需为 3–16 位字母、数字或下划线");
                        var accounts = Store.State["accounts"] as JArray ?? new JArray();
                        if (accounts.Any(item => string.Equals(item.Value<string>("name"), name, StringComparison.OrdinalIgnoreCase)))
                            throw new InvalidOperationException("此离线账户已添加");
                        var account = new JObject
                        {
                            ["id"] = Guid.NewGuid().ToString(),
                            ["name"] = name,
                            ["uuid"] = OfflineUuid(name),
                            ["expiresAt"] = 0,
                            ["kind"] = "offline"
                        };
                        accounts.Add(account);
                        Store.State["accounts"] = accounts;
                        Store.State["selectedAccount"] = account["id"];
                        Store.Save();
                        Form.SendEvent("snapshot", Form.BuildSnapshot());
                        return account;
                    }

                case "account.login":
                    {
                        var requested = (payload?.ToString() ?? "browser").ToLowerInvariant();
                        var loginMethod = requested == "device" ? "device" : "browser";
                        var inApp = false;
                        MicrosoftAuth.InAppOnly = requested == "inapp";
                        MicrosoftAuth.InAppOpener = url => { Form.NavigateAuth(url); inApp = true; return true; };
                        try
                        {
                            var account = await MicrosoftAuth.LoginAsync(loginMethod, (type, body) => Form.SendEvent(type, body));
                            var accounts = Store.State["accounts"] as JArray ?? new JArray();
                            var uuid = account.Value<string>("uuid");
                            // 只替换同一个 Minecraft 档案的旧记录，其他账号必须保留。
                            var cleaned = new JArray(accounts.Where(item => !string.Equals(item.Value<string>("uuid"), uuid, StringComparison.OrdinalIgnoreCase)));
                            cleaned.Add(account);
                            Store.State["accounts"] = cleaned;
                            Store.State["selectedAccount"] = account["id"];
                            Store.Save();
                            Form.SendEvent("snapshot", Form.BuildSnapshot());
                            return account;
                        }
                        catch (Exception error)
                        {
                            Store.State["authNotice"] = error.Message;
                            Store.Save();
                            throw;
                        }
                        finally
                        {
                            MicrosoftAuth.InAppOpener = null;
                            MicrosoftAuth.InAppOnly = false;
                            if (inApp) Form.ReturnToApp();
                        }
                    }

                case "app.takeNotice":
                    {
                        var notice = Store.State.Value<string>("authNotice") ?? string.Empty;
                        if (!string.IsNullOrEmpty(notice))
                        {
                            Store.State["authNotice"] = string.Empty;
                            Store.Save();
                        }
                        return new JValue(notice);
                    }

                case "account.cancel":
                    MicrosoftAuth.Cancel();
                    return new JValue(true);

                case "account.reopen":
                    {
                        if (string.IsNullOrEmpty(MicrosoftAuth.LastUrl)) throw new InvalidOperationException("当前没有等待授权的登录");
                        if (!MicrosoftAuth.OpenInBrowser(MicrosoftAuth.LastUrl)) throw new InvalidOperationException("无法启动默认浏览器，请改用「复制链接」手动打开");
                        return new JValue(true);
                    }

                case "account.copyCode":
                    {
                        if (string.IsNullOrEmpty(MicrosoftAuth.LastCode)) throw new InvalidOperationException("当前没有设备代码");
                        Clipboard.SetText(MicrosoftAuth.LastCode);
                        return new JValue(true);
                    }

                case "account.copyLink":
                    {
                        if (string.IsNullOrEmpty(MicrosoftAuth.LastUrl)) throw new InvalidOperationException("当前没有等待授权的登录");
                        Clipboard.SetText(MicrosoftAuth.LastUrl);
                        return new JValue(true);
                    }

                case "account.refresh":
                    {
                        var id = payload?.ToString();
                        var accounts = Store.State["accounts"] as JArray ?? new JArray();
                        var target = accounts.FirstOrDefault(item => item.Value<string>("id") == id);
                        if (target == null || target.Value<string>("kind") != "microsoft") return new JValue(false);
                        try
                        {
                            var info = await MicrosoftAuth.RefreshAccountAsync(id, CancellationToken.None);
                            target["name"] = info["name"];
                            target["uuid"] = info["uuid"];
                            target["skin"] = info["skin"];
                            target["cape"] = info["cape"];
                            Store.Save();
                            Form.SendEvent("snapshot", Form.BuildSnapshot());
                            return new JValue(true);
                        }
                        catch (Exception error)
                        {
                            Diagnostics.Write("account refresh failed: " + error.Message);
                            return new JValue(false);
                        }
                    }

                case "account.select":
                    {
                        var id = payload?.ToString();
                        var accounts = Store.State["accounts"] as JArray ?? new JArray();
                        if (!accounts.Any(item => item.Value<string>("id") == id)) throw new InvalidOperationException("账户不存在");
                        Store.State["selectedAccount"] = id;
                        Store.Save();
                        Form.SendEvent("snapshot", Form.BuildSnapshot());
                        return new JValue(true);
                    }

                case "account.remove":
                    {
                        var id = payload?.ToString();
                        var accounts = Store.State["accounts"] as JArray ?? new JArray();
                        var target = accounts.FirstOrDefault(item => item.Value<string>("id") == id);
                        if (target != null) accounts.Remove(target);
                        MicrosoftAuth.Remove(id);
                        Store.State["accounts"] = accounts;
                        if (Store.State.Value<string>("selectedAccount") == id)
                            Store.State["selectedAccount"] = accounts.FirstOrDefault()?.Value<string>("id") ?? string.Empty;
                        Store.Save();
                        Form.SendEvent("snapshot", Form.BuildSnapshot());
                        return new JValue(true);
                    }

                case "account.reset":
                    MicrosoftAuth.Reset();
                    Store.State["accounts"] = new JArray();
                    Store.State["selectedAccount"] = string.Empty;
                    Store.Save();
                    Form.SendEvent("snapshot", Form.BuildSnapshot());
                    return new JValue(true);

                case "open":
                    {
                        var kind = payload?.ToString();
                        string target;
                        switch (kind)
                        {
                            case "data": target = AppPaths.DataDir; break;
                            case "logs": target = AppPaths.LogDir; break;
                            case "website": target = "https://www.minecraft.net/"; break;
                            case "redeem": target = "https://www.minecraft.net/redeem"; break;
                            case "microsoft": target = "https://microsoft.com/devicelogin"; break;
                            case "registration": target = "https://portal.azure.com/#view/Microsoft_AAD_RegisteredApps/ApplicationsListBlade"; break;
                            default: throw new InvalidOperationException("不支持的打开目标");
                        }
                        if (kind == "data" || kind == "logs")
                            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + target + "\"") { UseShellExecute = true });
                        else
                            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                        return new JValue(true);
                    }

                case "java.detect":
                    return await DetectJava();

                case "java.choose":
                    {
                        using (var dialog = new OpenFileDialog { Title = "选择 Java 可执行文件", Filter = "Java 可执行文件|java.exe|所有文件|*.*" })
                        {
                            return dialog.ShowDialog(Form) == DialogResult.OK ? new JValue(dialog.FileName) : JValue.CreateNull();
                        }
                    }

                case "java.addFolder":
                    {
                        var picked = FolderPicker.Pick(Form.Handle, "选择 Java 所在目录（扫描最多 5 层）", AppPaths.BaseDir);
                        if (string.IsNullOrWhiteSpace(picked)) return JValue.CreateNull();
                        var settings = Store.Settings;
                        var roots = settings["javaRoots"] as JArray ?? new JArray();
                        var directory = InstanceScanner.canonical(picked);
                        if (!roots.Any(item => string.Equals(item.ToString(), directory, StringComparison.OrdinalIgnoreCase)))
                        {
                            if (roots.Count >= 30) throw new InvalidOperationException("最多添加 30 个 Java 搜索目录");
                            roots.Add(directory);
                            settings["javaRoots"] = roots;
                            Store.Save();
                            Form.SendEvent("snapshot", Form.BuildSnapshot());
                        }
                        return await DetectJava();
                    }

                case "java.removeFolder":
                    {
                        var settings = Store.Settings;
                        var roots = settings["javaRoots"] as JArray ?? new JArray();
                        var target = roots.FirstOrDefault(item => item.ToString() == payload?.ToString());
                        if (target != null) roots.Remove(target);
                        settings["javaRoots"] = roots;
                        Store.Save();
                        Form.SendEvent("snapshot", Form.BuildSnapshot());
                        return await DetectJava();
                    }

                case "instance.launch":
                    {
                        var instance = FindInstance(payload?.ToString());
                        return await Form.Launch.LaunchAsync(instance);
                    }

                case "instance.stop":
                    {
                        var instance = FindInstance(payload?.ToString());
                        var id = instance.Value<string>("id");
                        if (!Form.Launch.IsRunning(id)) throw new InvalidOperationException("游戏尚未运行");
                        var confirm = MessageBox.Show(Form, "结束“" + instance.Value<string>("name") + "”的游戏进程？\n\n尚未保存的游戏进度可能丢失，建议优先在游戏中退出。",
                            "结束游戏", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning);
                        if (confirm != DialogResult.OK) return new JValue(false);
                        Form.Launch.Stop(id);
                        return new JValue(true);
                    }

                // 下载、资源与模组
                case "versions": return await VersionsAsync();

                case "install":
                    {
                        var version = payload?.Value<string>("version")?.Trim();
                        var loader = (payload?.Value<string>("loader") ?? "vanilla").ToLowerInvariant();
                        var loaderVersion = payload?.Value<string>("loaderVersion") ?? string.Empty;
                        var optifine = payload?.Value<string>("optifine") ?? string.Empty;
                        if (string.IsNullOrEmpty(version)) throw new InvalidOperationException("请选择游戏版本");
                        if (loader != "vanilla" && string.IsNullOrEmpty(loaderVersion))
                            throw new InvalidOperationException("请选择 " + loader + " 版本");
                        var root = Path.Combine(AppPaths.BaseDir, "minecraft");
                        Directory.CreateDirectory(Path.Combine(root, "versions"));
                        var sources = Store.State["sources"] as JArray ?? new JArray();
                        if (!sources.Any(item => string.Equals(item.Value<string>("path"), root, StringComparison.OrdinalIgnoreCase)))
                        {
                            sources.Add(new JObject { ["id"] = Guid.NewGuid().ToString(), ["path"] = root, ["label"] = "便携游戏目录" });
                            Store.State["sources"] = sources;
                            Store.Save();
                            Form.Rescan();
                        }
                        var javaPath = loader == "forge" || loader == "neoforge" || !string.IsNullOrEmpty(optifine) ? EnsureJava() : null;
                        return new JValue(Form.Launch.StartInstall(version, loader, loaderVersion, root, javaPath, optifine));
                    }

                case "loaders.list":
                    {
                        var minecraft = payload?.Value<string>("minecraft") ?? string.Empty;
                        var loader = payload?.Value<string>("loader") ?? "vanilla";
                        try
                        {
                            var catalog = await Loaders.ListAsync(minecraft, loader, CancellationToken.None);
                            Diagnostics.Write("loaders.list " + minecraft + " / " + loader + " => " + catalog.Count);
                            return catalog;
                        }
                        catch (Exception error)
                        {
                            Diagnostics.Write("loaders.list " + minecraft + " / " + loader + " failed: " + error);
                            throw;
                        }
                    }

                case "resource.search":
                    return await Resources.SearchAsync(payload as JObject ?? new JObject(), CancellationToken.None);

                case "resource.versions":
                    {
                        var selection = payload?["selection"] as JObject ?? new JObject();
                        var instanceId = payload?.Value<string>("instanceId");
                        var instance = string.IsNullOrEmpty(instanceId) ? null : FindInstance(instanceId);
                        return await Resources.VersionsAsync(selection, instance, CancellationToken.None);
                    }

                case "resource.plan":
                    {
                        var selection = payload?["selection"] as JObject ?? new JObject();
                        var instance = FindInstance(payload?.Value<string>("instanceId"));
                        return await Resources.PlanAsync(selection, instance, CancellationToken.None);
                    }

                case "resource.install":
                    {
                        var selection = payload?["selection"] as JObject ?? new JObject();
                        var instance = FindInstance(payload?.Value<string>("instanceId"));
                        return new JValue(Form.Launch.StartResourceInstall(selection, instance, Resources));
                    }

                case "resource.configure":
                    if (payload == null || payload.Type == JTokenType.Null) return new JValue(Resources.Configured());
                    return new JValue(Resources.Configure(payload.ToString()));

                case "resource.open":
                    return new JValue(await Resources.ProjectUrlAsync(payload as JObject ?? new JObject(), CancellationToken.None));

                case "plugins.list": return new JArray();

                case "mods.list":
                    return Mods.List(FindInstance(payload?.ToString()));

                case "mods.import":
                    {
                        var instance = FindInstance(payload?.ToString());
                        using (var dialog = new OpenFileDialog
                        {
                            Title = "导入本地模组",
                            Filter = "Minecraft 模组|*.jar",
                            Multiselect = true
                        })
                        {
                            if (dialog.ShowDialog(Form) != DialogResult.OK) return JValue.CreateNull();
                            Mods.Import(instance, dialog.FileNames);
                            return Mods.List(instance);
                        }
                    }

                case "mods.toggle":
                    {
                        var instance = FindInstance(payload?.Value<string>("id"));
                        Mods.Toggle(instance, payload.Value<string>("file"));
                        return Mods.List(instance);
                    }

                case "mods.delete":
                    {
                        var instance = FindInstance(payload?.Value<string>("id"));
                        var file = payload.Value<string>("file");
                        var confirm = MessageBox.Show(Form, "将 " + file + " 移至回收站？", "删除模组",
                            MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                        if (confirm != DialogResult.OK) return JValue.CreateNull();
                        Mods.Delete(instance, file);
                        return Mods.List(instance);
                    }

                case "task.cancel": return new JValue(Form.Launch.CancelTask(payload?.ToString()));
                case "logs.export": return new JValue(false);

                case "window.minimize": Form.WindowState = FormWindowState.Minimized; return new JValue(true);
                case "window.maximize": Form.ToggleMaximize(); return new JValue(true);
                case "window.close":
                    Form.SendEvent("closeRequested", null);
                    return new JValue(true);
                case "window.closeReady":
                    Form.CloseReady();
                    return new JValue(true);

                default:
                    throw new NotSupportedException("当前是 C# 重构预览版，此功能尚未接入：" + method);
            }
        }

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private static JArray versionCache;

        /// <summary>Mojang 官方版本清单（下载页的“游戏源”）。</summary>
        private static async Task<JToken> VersionsAsync()
        {
            if (versionCache != null) return versionCache;
            var text = await Http.GetStringAsync("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
            var root = JObject.Parse(text);
            var list = new JArray();
            foreach (var version in root["versions"] as JArray ?? new JArray())
            {
                list.Add(new JObject
                {
                    ["id"] = version.Value<string>("id"),
                    ["type"] = version.Value<string>("type"),
                    ["releaseTime"] = version.Value<string>("releaseTime"),
                    ["url"] = version.Value<string>("url")
                });
            }
            versionCache = list;
            return list;
        }

        private static string EnsureJava()
        {
            var settings = Store.Settings;
            var configured = settings.Value<string>("java");
            if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
            var roots = (settings["javaRoots"] as JArray ?? new JArray()).Select(item => item.ToString()).ToList();
            var installed = JavaDetector.Discover(new[] { configured }, roots);
            if (installed.Count == 0) throw new InvalidOperationException("安装 Forge/NeoForge 需要 Java，请先在工具箱中检测 Java");
            return installed.OrderByDescending(item => item.Value<int>("major")).First().Value<string>("path");
        }

        private static Task<JToken> DetectJava()
        {
            var settings = Store.Settings;
            var extra = new List<string> { settings.Value<string>("java") };
            var options = Store.State["options"] as JObject;
            if (options != null)
                foreach (var property in options.Properties())
                {
                    var value = property.Value?["java"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(value)) extra.Add(value);
                }
            var roots = (settings["javaRoots"] as JArray ?? new JArray()).Select(item => item.ToString()).ToList();
            return Task.Run<JToken>(() => new JArray(JavaDetector.Discover(extra, roots)));
        }

        private static JObject FindInstance(string id)
        {
            var instance = Form.Rescan().Instances.FirstOrDefault(item => item.Value<string>("id") == id);
            if (instance == null) throw new InvalidOperationException("此实例已不存在，请重新扫描");
            return instance;
        }

        private static string OfflineUuid(string name)
        {
            using (var md5 = MD5.Create())
            {
                var hash = md5.ComputeHash(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
                hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
                hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
                var hex = BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
                return hex.Substring(0, 8) + "-" + hex.Substring(8, 4) + "-" + hex.Substring(12, 4) + "-" + hex.Substring(16, 4) + "-" + hex.Substring(20, 12);
            }
        }
    }
}
