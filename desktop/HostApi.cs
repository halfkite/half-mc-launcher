using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;

namespace HalfMcLauncher
{
    /// <summary>
    /// 第一版只接入“外壳 + 浏览实例 + 设置”所需的方法；其余方法返回空数据或明确提示，
    /// 避免界面在未实现的页面上崩溃。
    /// </summary>
    internal static class HostApi
    {
        private static MainForm Form { get { return Program.MainWindow; } }
        private static ConfigStore Store { get { return Program.MainWindow.Store; } }

        internal static JToken Handle(string method, JToken payload)
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
                        using (var dialog = new FolderBrowserDialog { Description = "选择 Minecraft 目录、versions 文件夹，或包含多个游戏目录的容器" })
                        {
                            if (dialog.ShowDialog(Form) != DialogResult.OK) return JValue.CreateNull();
                            var directory = InstanceScanner.canonical(dialog.SelectedPath);
                            var sources = Store.State["sources"] as JArray ?? new JArray();
                            if (sources.Any(item => string.Equals(item.Value<string>("path"), directory, StringComparison.OrdinalIgnoreCase)))
                                throw new InvalidOperationException("此目录已经添加");
                            InstanceScanner.FindRoots(directory);
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
                        foreach (var key in new[] { "theme", "accent", "reduceMotion", "fontScale" })
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
                        Store.State["accounts"] = accounts;
                        if (Store.State.Value<string>("selectedAccount") == id)
                            Store.State["selectedAccount"] = accounts.FirstOrDefault()?.Value<string>("id") ?? string.Empty;
                        Store.Save();
                        Form.SendEvent("snapshot", Form.BuildSnapshot());
                        return new JValue(true);
                    }

                case "account.reset":
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

                // 以下页面尚未接入：返回空数据，界面显示空状态而不是报错。
                case "versions": return new JArray();
                case "loaders.list": return new JArray();
                case "plugins.list": return new JArray();
                case "mods.list": return new JArray();
                case "java.detect": return new JArray();
                case "task.cancel": return new JValue(true);
                case "resource.configure": return new JValue(false);
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
