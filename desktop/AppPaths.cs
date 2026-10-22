using System;
using System.IO;

namespace HalfMcLauncher
{
    /// <summary>
    /// 便携路径约定与 Electron 版保持一致：数据放在可执行文件旁的 data/。
    /// 环境变量 HALF_DATA_DIR（兼容旧的 HAKF_DATA_DIR）用于测试与调试。
    /// </summary>
    internal static class AppPaths
    {
        public static string BaseDir { get; private set; }
        public static string DataDir { get; private set; }
        public static string WebRoot { get; private set; }
        public static string ConfigFile { get { return Path.Combine(DataDir, "config.json"); } }
        public static string LogDir { get { return Path.Combine(DataDir, "logs"); } }

        public static void Initialize()
        {
            BaseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

            var overrideDir = Environment.GetEnvironmentVariable("HALF_DATA_DIR");
            if (string.IsNullOrWhiteSpace(overrideDir)) overrideDir = Environment.GetEnvironmentVariable("HAKF_DATA_DIR");
            DataDir = string.IsNullOrWhiteSpace(overrideDir)
                ? Path.Combine(BaseDir, "data")
                : Path.GetFullPath(overrideDir);

            try
            {
                Directory.CreateDirectory(DataDir);
            }
            catch (Exception error) when (error is UnauthorizedAccessException || error is IOException)
            {
                // 放在 Program Files、网络盘或只读位置时退回用户目录，避免直接无法启动。
                DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HalfMcLauncher", "data");
                Directory.CreateDirectory(DataDir);
            }

            WebRoot = Path.Combine(DataDir, "web");

            Directory.CreateDirectory(LogDir);
            Directory.CreateDirectory(Path.Combine(DataDir, "webview2"));
        }
    }
}
