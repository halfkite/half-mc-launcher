using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Newtonsoft.Json.Linq;

namespace HalfMcLauncher
{
    /// <summary>
    /// 读取存档 level.dat（gzip NBT）中的版本、游戏模式、上次游玩时间与图标。
    /// </summary>
    internal static class SaveInfo
    {
        private sealed class Reader
        {
            private readonly Stream stream;
            public Reader(Stream stream) { this.stream = stream; }
            private byte Byte() { var value = stream.ReadByte(); if (value < 0) throw new EndOfStreamException(); return (byte)value; }
            private byte[] Bytes(int count)
            {
                var buffer = new byte[count];
                var offset = 0;
                while (offset < count)
                {
                    var read = stream.Read(buffer, offset, count - offset);
                    if (read <= 0) throw new EndOfStreamException();
                    offset += read;
                }
                return buffer;
            }
            public int Int() { return Byte() << 24 | Byte() << 16 | Byte() << 8 | Byte(); }
            private long Long() { return (long)Int() << 32 | (uint)Int(); }
            public string Text()
            {
                var length = Byte() << 8 | Byte();
                return length == 0 ? string.Empty : Encoding.UTF8.GetString(Bytes(length));
            }
            public object Read(byte type)
            {
                switch (type)
                {
                    case 1: return (sbyte)Byte();
                    case 2: return (short)(Byte() << 8 | Byte());
                    case 3: return Int();
                    case 4: return Long();
                    case 5: { var bytes = new[] { Byte(), Byte(), Byte(), Byte() }; Array.Reverse(bytes); return BitConverter.ToSingle(bytes, 0); }
                    case 6: { var bytes = Bytes(8); Array.Reverse(bytes); return BitConverter.ToDouble(bytes, 0); }
                    case 7: return Bytes(Int());
                    case 8: return Text();
                    case 9:
                        {
                            var itemType = Byte();
                            var count = Int();
                            var list = new List<object>();
                            for (var index = 0; index < count; index++) list.Add(Read(itemType));
                            return list;
                        }
                    case 10:
                        {
                            var map = new Dictionary<string, object>(StringComparer.Ordinal);
                            while (true)
                            {
                                var child = Byte();
                                if (child == 0) break;
                                var name = Text();
                                map[name] = Read(child);
                            }
                            return map;
                        }
                    case 11: { var count = Int(); var array = new int[count]; for (var index = 0; index < count; index++) array[index] = Int(); return array; }
                    case 12: { var count = Int(); var array = new long[count]; for (var index = 0; index < count; index++) array[index] = Long(); return array; }
                    default: return null;
                }
            }
        }

        public static JObject Read(string folder)
        {
            var result = new JObject();
            var level = Path.Combine(folder, "level.dat");
            if (File.Exists(level))
            {
                try
                {
                    using (var file = File.OpenRead(level))
                    using (var gzip = new GZipStream(file, CompressionMode.Decompress))
                    using (var memory = new MemoryStream())
                    {
                        gzip.CopyTo(memory);
                        memory.Seek(0, SeekOrigin.Begin);
                        var reader = new Reader(memory);
                        reader.Read(1);
                        reader.Text();
                        var root = reader.Read(10) as Dictionary<string, object>;
                        var data = root != null && root.TryGetValue("Data", out var rawData) ? rawData as Dictionary<string, object> : null;
                        if (data != null)
                        {
                            if (data.TryGetValue("Version", out var rawVersion) && rawVersion is Dictionary<string, object> version)
                                result["version"] = version.TryGetValue("Name", out var name) ? name?.ToString() ?? string.Empty : string.Empty;
                            if (data.TryGetValue("LastPlayed", out var played) && played is long ticks)
                                result["lastPlayed"] = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ticks).ToString("o");
                            if (data.TryGetValue("GameType", out var gameType) && gameType is int mode)
                                result["gameType"] = mode;
                            if (data.TryGetValue("hardcore", out var hardcore) && hardcore is sbyte hardcoreValue)
                                result["hardcore"] = hardcoreValue != 0;
                            if (data.TryGetValue("LevelName", out var levelName))
                                result["levelName"] = levelName?.ToString() ?? string.Empty;
                        }
                    }
                }
                catch (Exception error)
                {
                    result["issue"] = "无法读取 level.dat：" + error.Message;
                }
            }
            var icon = Path.Combine(folder, "icon.png");
            if (File.Exists(icon) && new FileInfo(icon).Length < 2 * 1024 * 1024)
            {
                try { result["icon"] = "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(icon)); }
                catch { }
            }
            return result;
        }
    }
}
