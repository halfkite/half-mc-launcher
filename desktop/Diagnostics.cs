using System;
using System.IO;

namespace HalfMcLauncher
{
    /// <summary>启动阶段的轻量日志，便于在没有控制台时定位“窗口没出现”的问题。</summary>
    internal static class Diagnostics
    {
        private static readonly object Gate = new object();

        public static void Write(string message)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(AppPaths.LogDir);
                    File.AppendAllText(Path.Combine(AppPaths.LogDir, "startup.log"),
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + message + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}
