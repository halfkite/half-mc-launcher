using System;
using Newtonsoft.Json.Linq;

namespace HalfMcLauncher
{
    /// <summary>
    /// 全局下载设置：并发数、失败重试、请求超时与默认下载源。
    /// 在启动器尚未创建时（CLI 自检）回落到默认值。
    /// </summary>
    internal static class DownloadSettings
    {
        private static JObject Data
        {
            get
            {
                try { return Program.MainWindow?.Store.Settings["download"] as JObject ?? new JObject(); }
                catch { return new JObject(); }
            }
        }

        private static int Value(string key, int fallback, int min, int max)
        {
            var raw = Data.Value<int?>(key) ?? fallback;
            return Math.Max(min, Math.Min(max, raw));
        }

        public static string Source => Data.Value<string>("source") ?? "auto";
        public static int Threads => Value("threads", 4, 1, 8);
        public static int Retries => Value("retries", 2, 0, 5);
        public static int TimeoutSeconds => Value("timeout", 60, 15, 300);
    }
}
