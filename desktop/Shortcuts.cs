using System;
using System.IO;
using System.Text;
using Newtonsoft.Json.Linq;

namespace HalfMcLauncher
{
    /// <summary>
    /// 为实例导出启动入口：Windows 快捷方式（.lnk）或批处理脚本（.bat）。
    /// 两者都会带 --instance 参数启动本启动器，由启动器自动开始该实例。
    /// </summary>
    internal static class Shortcuts
    {
        private static string Executable => System.Windows.Forms.Application.ExecutablePath;

        public static JObject Export(JObject instance, string kind, string path)
        {
            var id = instance.Value<string>("id");
            var name = instance.Value<string>("name") ?? "实例";
            if (string.IsNullOrEmpty(id)) throw new InvalidOperationException("实例无效");
            if (kind == "bat")
            {
                var content = "@echo off\r\nchcp 65001 >nul\r\nstart \"\" \"" + Executable + "\" --instance " + id + "\r\n";
                File.WriteAllText(path, content, new UTF8Encoding(false));
            }
            else
            {
                var type = Type.GetTypeFromProgID("WScript.Shell");
                if (type == null) throw new InvalidOperationException("当前系统不支持创建快捷方式");
                dynamic shell = Activator.CreateInstance(type);
                var link = shell.CreateShortcut(path);
                link.TargetPath = Executable;
                link.Arguments = "--instance " + id;
                link.WorkingDirectory = AppPaths.BaseDir;
                link.IconLocation = Executable + ",0";
                link.Description = "Half Mc Launcher · " + name;
                link.Save();
            }
            return new JObject { ["path"] = path, ["kind"] = kind, ["instance"] = id };
        }
    }
}
