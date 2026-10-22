using System;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HalfMcLauncher
{
    /// <summary>
    /// 读取/保存与 Electron 版完全相同的 data/config.json。
    /// 只替换自己认识的节点，其余字段原样保留，避免两个版本互相破坏配置。
    /// </summary>
    internal sealed class ConfigStore
    {
        private readonly object gate = new object();
        public JObject Root { get; private set; }
        public string Warning { get; private set; }

        public static ConfigStore Load()
        {
            var store = new ConfigStore();
            try
            {
                if (File.Exists(AppPaths.ConfigFile))
                {
                    var text = File.ReadAllText(AppPaths.ConfigFile, Encoding.UTF8);
                    store.Root = JObject.Parse(text);
                }
            }
            catch (Exception error)
            {
                var backup = AppPaths.ConfigFile + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                try { File.Copy(AppPaths.ConfigFile, backup, true); } catch { }
                store.Warning = "配置文件损坏，已备份为 " + Path.GetFileName(backup) + "，并使用默认配置：" + error.Message;
                store.Root = null;
            }
            if (store.Root == null) store.Root = DefaultState();
            StateDefaults.Apply(store.Root);
            return store;
        }

        public JObject State { get { return Root; } }

        /// <summary>测试与迁移用：直接替换整份状态。</summary>
        internal void ReplaceRoot(JObject state)
        {
            Root = state ?? DefaultState();
            StateDefaults.Apply(Root);
        }

        public JObject Settings
        {
            get
            {
                var settings = Root["settings"] as JObject;
                if (settings == null) { settings = new JObject(); Root["settings"] = settings; }
                return settings;
            }
        }

        public JObject Workspace
        {
            get
            {
                var workspace = Root["workspace"] as JObject;
                if (workspace == null) { workspace = DefaultState()["workspace"] as JObject; Root["workspace"] = workspace; }
                return workspace;
            }
        }

        public void Save()
        {
            lock (gate)
            {
                var json = Root.ToString(Formatting.Indented);
                var temp = AppPaths.ConfigFile + ".tmp";
                File.WriteAllText(temp, json, new UTF8Encoding(false));
                if (File.Exists(AppPaths.ConfigFile)) File.Delete(AppPaths.ConfigFile);
                File.Move(temp, AppPaths.ConfigFile);
            }
        }

        internal static JObject DefaultState()
        {
            var settings = new JObject
            {
                ["theme"] = "light",
                ["accent"] = "#74b5ff",
                ["memory"] = 4096,
                ["java"] = "",
                ["clientId"] = "",
                ["reduceMotion"] = false,
                ["fontScale"] = 100,
                ["opacity"] = 100,
                ["glass"] = true,
                ["memoryMode"] = "auto",
                ["download"] = new JObject { ["source"] = "auto", ["threads"] = 4, ["retries"] = 2, ["timeout"] = 60 },
                ["javaRoots"] = new JArray()
            };

            var panes = new JArray();
            for (var index = 0; index < 4; index++)
            {
                var tabId = Guid.NewGuid().ToString();
                var tab = new JObject
                {
                    ["id"] = tabId,
                    ["page"] = index == 1 ? "install" : "instances",
                    ["pinned"] = false,
                    ["query"] = "",
                    ["group"] = "",
                    ["scroll"] = 0,
                    ["view"] = new JObject()
                };
                panes.Add(new JObject
                {
                    ["id"] = "pane-" + index,
                    ["active"] = tabId,
                    ["tabs"] = new JArray(tab)
                });
            }

            var workspace = new JObject
            {
                ["layout"] = "single",
                ["panes"] = panes,
                ["focused"] = "pane-0",
                ["ratios"] = new JArray(50, 50, 50),
                ["sidebarWidth"] = 210,
                ["sidebarCollapsed"] = false,
                ["sidebarExpandedWidth"] = 210,
                ["closedTabs"] = new JArray()
            };

            return new JObject
            {
                ["schemaVersion"] = 1,
                ["workspace"] = workspace,
                ["presets"] = new JArray(),
                ["sources"] = new JArray(),
                ["options"] = new JObject(),
                ["hidden"] = new JArray(),
                ["accounts"] = new JArray(),
                ["selectedAccount"] = "",
                ["settings"] = settings,
                ["lastPlayed"] = new JObject(),
                ["windowBounds"] = new JObject
                {
                    ["width"] = 1280,
                    ["height"] = 820,
                    ["maximized"] = false
                }
            };
        }
    }

    internal static class StateDefaults
    {
        /// <summary>补齐缺失的顶层节点，兼容旧配置。</summary>
        public static void Apply(JObject root)
        {
            var template = ConfigStore.DefaultState();
            foreach (var property in template.Properties())
            {
                if (root[property.Name] == null || root[property.Name].Type == JTokenType.Null)
                    root[property.Name] = property.Value.DeepClone();
            }
            var settings = root["settings"] as JObject;
            if (settings != null)
            {
                foreach (var property in (template["settings"] as JObject).Properties())
                {
                    if (settings[property.Name] == null)
                        settings[property.Name] = property.Value.DeepClone();
                }
            }
        }
    }
}
