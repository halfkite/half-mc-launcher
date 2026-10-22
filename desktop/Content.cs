using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.VisualBasic.FileIO;
using Newtonsoft.Json.Linq;

namespace HalfMcLauncher
{
    /// <summary>
    /// 实例内容管理：资源包、光影、存档、截图，以及存档备份/还原。
    /// </summary>
    internal static class Content
    {
        private static readonly string[] Kinds = { "resourcepacks", "shaderpacks", "saves", "screenshots" };

        private static string Folder(JObject instance, string kind)
        {
            if (!Kinds.Contains(kind)) throw new InvalidOperationException("不支持的内容类型");
            return Mods.ResourceDirectory(instance, kind);
        }

        private static long Size(string path)
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            var total = 0L;
            foreach (var file in Directory.EnumerateFiles(path, "*", System.IO.SearchOption.AllDirectories))
            {
                try { total += new FileInfo(file).Length; } catch { }
            }
            return total;
        }

        private static string Display(string name) =>
            Path.GetFileNameWithoutExtension(name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - 9) : name);

        public static JArray List(JObject instance, string kind)
        {
            var directory = Folder(instance, kind);
            var list = new JArray();
            if (!Directory.Exists(directory)) return list;
            if (kind == "saves")
            {
                foreach (var save in Directory.EnumerateDirectories(directory))
                {
                    var info = new DirectoryInfo(save);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    list.Add(new JObject
                    {
                        ["file"] = info.Name,
                        ["name"] = info.Name,
                        ["size"] = Size(save),
                        ["modified"] = info.LastWriteTimeUtc.ToString("o"),
                        ["kind"] = kind,
                        ["directory"] = true,
                        ["enabled"] = true
                    });
                }
            }
            else
            {
                var pattern = kind == "screenshots" ? new[] { ".png", ".jpg", ".jpeg" } : new[] { ".zip" };
                foreach (var file in Directory.EnumerateFiles(directory).Where(name => pattern.Contains(Path.GetExtension(name).ToLowerInvariant())))
                {
                    var info = new FileInfo(file);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    var enabled = !info.Name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                    list.Add(new JObject
                    {
                        ["file"] = info.Name,
                        ["name"] = Display(info.Name),
                        ["size"] = info.Length,
                        ["modified"] = info.LastWriteTimeUtc.ToString("o"),
                        ["kind"] = kind,
                        ["directory"] = false,
                        ["enabled"] = enabled
                    });
                }
                if (kind != "screenshots")
                {
                    foreach (var folder in Directory.EnumerateDirectories(directory))
                    {
                        var info = new DirectoryInfo(folder);
                        if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        list.Add(new JObject
                        {
                            ["file"] = info.Name,
                            ["name"] = info.Name,
                            ["size"] = Size(folder),
                            ["modified"] = info.LastWriteTimeUtc.ToString("o"),
                            ["kind"] = kind,
                            ["directory"] = true,
                            ["enabled"] = true
                        });
                    }
                }
            }
            return new JArray(list.OrderBy(item => item.Value<string>("name"), StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("zh-CN"), true)));
        }

        public static JArray Toggle(JObject instance, string kind, string file)
        {
            if (kind == "saves" || kind == "screenshots") throw new InvalidOperationException("该类型不支持启用/禁用");
            var directory = Folder(instance, kind);
            var target = Path.Combine(directory, Mods.SafeFilename(file));
            if (!File.Exists(target)) throw new InvalidOperationException("内容不存在");
            var next = target.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                ? target.Substring(0, target.Length - 9)
                : target + ".disabled";
            if (File.Exists(next)) { File.SetAttributes(next, FileAttributes.Normal); File.Delete(next); }
            if ((File.GetAttributes(target) & FileAttributes.ReadOnly) != 0) File.SetAttributes(target, FileAttributes.Normal);
            try { File.Move(target, next); }
            catch (Exception error) when (error is UnauthorizedAccessException || error is IOException)
            {
                throw new InvalidOperationException("无法修改文件，请确认目录可写：" + target, error);
            }
            return List(instance, kind);
        }

        public static JArray Import(JObject instance, string kind, IEnumerable<string> files)
        {
            var directory = Folder(instance, kind);
            foreach (var source in files)
            {
                var name = Path.GetFileName(source.TrimEnd('\\', '/'));
                if (kind == "saves")
                {
                    if (!Directory.Exists(source)) throw new InvalidOperationException("存档需要选择文件夹");
                    var destination = Path.Combine(directory, Mods.SafeFilename(name));
                    if (Directory.Exists(destination)) throw new InvalidOperationException("已存在同名存档：" + name);
                    CopyDirectory(source, destination);
                }
                else
                {
                    if (!File.Exists(source)) throw new InvalidOperationException("文件不存在：" + name);
                    if (kind == "screenshots" && !new[] { ".png", ".jpg", ".jpeg" }.Contains(Path.GetExtension(name).ToLowerInvariant()))
                        throw new InvalidOperationException("只支持图片");
                    if (kind != "screenshots" && !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("只支持 ZIP 文件");
                    var destination = Path.Combine(directory, Mods.SafeFilename(name));
                    if (File.Exists(destination)) throw new InvalidOperationException("已存在同名文件：" + name);
                    File.Copy(source, destination);
                }
            }
            return List(instance, kind);
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.EnumerateFiles(source))
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            foreach (var folder in Directory.EnumerateDirectories(source))
                CopyDirectory(folder, Path.Combine(destination, Path.GetFileName(folder)));
        }

        public static JArray Delete(JObject instance, string kind, string file)
        {
            var directory = Folder(instance, kind);
            var target = Path.Combine(directory, Mods.SafeFilename(file));
            if (Directory.Exists(target))
                FileSystem.DeleteDirectory(target, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            else if (File.Exists(target))
                FileSystem.DeleteFile(target, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            else throw new InvalidOperationException("内容不存在");
            return List(instance, kind);
        }

        public static void Reveal(JObject instance, string kind, string file)
        {
            var directory = Folder(instance, kind);
            var target = Path.Combine(directory, Mods.SafeFilename(file));
            if (!File.Exists(target) && !Directory.Exists(target))
                System.Diagnostics.Process.Start("explorer.exe", "\"" + directory + "\"");
            else
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + target + "\"") { UseShellExecute = true });
        }

        private static string BackupDirectory(JObject instance) => Path.Combine(instance.Value<string>("gameDir"), "backups");

        public static JArray Backups(JObject instance)
        {
            var directory = BackupDirectory(instance);
            var list = new JArray();
            if (!Directory.Exists(directory)) return list;
            foreach (var file in Directory.EnumerateFiles(directory, "*.zip").OrderByDescending(File.GetLastWriteTimeUtc))
            {
                var info = new FileInfo(file);
                list.Add(new JObject { ["file"] = info.Name, ["size"] = info.Length, ["modified"] = info.LastWriteTimeUtc.ToString("o") });
            }
            return list;
        }

        public static JArray BackupSave(JObject instance, string name)
        {
            var saves = Folder(instance, "saves");
            var source = Path.Combine(saves, Mods.SafeFilename(name));
            if (!Directory.Exists(source)) throw new InvalidOperationException("存档不存在");
            var directory = BackupDirectory(instance);
            Directory.CreateDirectory(directory);
            var target = Path.Combine(directory, name + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip");
            new FastZip().CreateZip(target, source, true, null);
            return Backups(instance);
        }

        public static JArray RestoreSave(JObject instance, string backup)
        {
            var directory = BackupDirectory(instance);
            var source = Path.Combine(directory, Mods.SafeFilename(backup));
            if (!File.Exists(source)) throw new InvalidOperationException("备份不存在");
            var saves = Folder(instance, "saves");
            var name = Path.GetFileNameWithoutExtension(backup);
            var target = Path.Combine(saves, name);
            if (Directory.Exists(target))
                FileSystem.DeleteDirectory(target, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            new FastZip().ExtractZip(source, target, null);
            return List(instance, "saves");
        }
    }
}
