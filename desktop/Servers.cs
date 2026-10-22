using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace HalfMcLauncher
{
    /// <summary>
    /// 服务器列表：读写 servers.dat（NBT，可能是 gzip）。NBT 使用大端序与两字节长度前缀。
    /// </summary>
    internal static class Servers
    {
        private static string FilePath(JObject instance)
        {
            var gameDir = InstanceScanner.canonical(instance.Value<string>("gameDir"));
            return Path.Combine(gameDir, "servers.dat");
        }

        private sealed class NbtReader
        {
            private readonly Stream stream;
            public NbtReader(Stream stream) { this.stream = stream; }

            public byte ReadByte() { var value = stream.ReadByte(); if (value < 0) throw new EndOfStreamException(); return (byte)value; }

            private byte[] ReadBytes(int count)
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

            public string ReadName()
            {
                var length = (int)((uint)ReadByte() << 8 | ReadByte());
                if (length == 0) return string.Empty;
                return Encoding.UTF8.GetString(ReadBytes(length));
            }

            public object Read(byte type)
            {
                switch (type)
                {
                    case 1: return (sbyte)ReadByte();
                    case 2: return (short)(ReadByte() << 8 | ReadByte());
                    case 3: return ReadByte() << 24 | ReadByte() << 16 | ReadByte() << 8 | ReadByte();
                    case 4:
                        {
                            long high = (uint)ReadByte() << 24 | (uint)ReadByte() << 16 | (uint)ReadByte() << 8 | ReadByte();
                            long low = (uint)ReadByte() << 24 | (uint)ReadByte() << 16 | (uint)ReadByte() << 8 | ReadByte();
                            return high << 32 | low;
                        }
                    case 5:
                        {
                            var bytes = new[] { ReadByte(), ReadByte(), ReadByte(), ReadByte() };
                            Array.Reverse(bytes);
                            return BitConverter.ToSingle(bytes, 0);
                        }
                    case 6:
                        {
                            var bytes = new byte[8];
                            for (var index = 0; index < 8; index++) bytes[index] = ReadByte();
                            Array.Reverse(bytes);
                            return BitConverter.ToDouble(bytes, 0);
                        }
                    case 7: return ReadBytes(ReadInt());
                    case 8: return ReadName();
                    case 9:
                        {
                            var itemType = ReadByte();
                            var count = ReadInt();
                            var list = new List<object>();
                            for (var index = 0; index < count; index++) list.Add(Read(itemType));
                            return list;
                        }
                    case 10:
                        {
                            var map = new Dictionary<string, object>(StringComparer.Ordinal);
                            while (true)
                            {
                                var childType = ReadByte();
                                if (childType == 0) break;
                                var name = ReadName();
                                map[name] = Read(childType);
                            }
                            return map;
                        }
                    case 11:
                        {
                            var count = ReadInt();
                            var array = new int[count];
                            for (var index = 0; index < count; index++) array[index] = ReadInt();
                            return array;
                        }
                    case 12:
                        {
                            var count = ReadInt();
                            var array = new long[count];
                            for (var index = 0; index < count; index++) array[index] = (long)Read(4);
                            return array;
                        }
                    default: return null;
                }
            }

            public int ReadInt() { return ReadByte() << 24 | ReadByte() << 16 | ReadByte() << 8 | ReadByte(); }
        }

        private static Dictionary<string, object> Read(string path)
        {
            if (!File.Exists(path)) return new Dictionary<string, object>();
            using (var stream = File.OpenRead(path))
            {
                var buffered = new BufferedStream(stream);
                var first = buffered.ReadByte();
                buffered.Seek(0, SeekOrigin.Begin);
                if (first == 0x1f)
                {
                    using (var gzip = new GZipStream(buffered, CompressionMode.Decompress))
                    using (var memory = new MemoryStream())
                    {
                        gzip.CopyTo(memory);
                        memory.Seek(0, SeekOrigin.Begin);
                        return Parse(memory);
                    }
                }
                return Parse(buffered);
            }
        }

        private static Dictionary<string, object> Parse(Stream stream)
        {
            var reader = new NbtReader(stream);
            if (reader.ReadByte() != 10) throw new InvalidOperationException("servers.dat 格式不正确");
            reader.ReadName();
            return (Dictionary<string, object>)reader.Read(10);
        }

        private static JArray Entries(JObject instance)
        {
            var root = Read(FilePath(instance));
            var list = new JArray();
            if (!root.TryGetValue("servers", out var raw) || !(raw is List<object> servers)) return list;
            foreach (var item in servers.OfType<Dictionary<string, object>>())
            {
                list.Add(new JObject
                {
                    ["name"] = item.TryGetValue("name", out var name) ? name?.ToString() ?? string.Empty : string.Empty,
                    ["ip"] = item.TryGetValue("ip", out var ip) ? ip?.ToString() ?? string.Empty : string.Empty,
                    ["icon"] = item.TryGetValue("icon", out var icon) ? icon?.ToString() ?? string.Empty : string.Empty
                });
            }
            return list;
        }

        public static JArray List(JObject instance) => Entries(instance);

        private sealed class NbtWriter
        {
            private readonly Stream stream;
            public NbtWriter(Stream stream) { this.stream = stream; }

            public void Byte(byte value) { stream.WriteByte(value); }
            public void Short(short value) { Byte((byte)(value >> 8)); Byte((byte)value); }
            public void Int(int value) { Byte((byte)(value >> 24)); Byte((byte)(value >> 16)); Byte((byte)(value >> 8)); Byte((byte)value); }
            public void String(string value)
            {
                var bytes = Encoding.UTF8.GetBytes(value ?? string.Empty);
                Short((short)bytes.Length);
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        private static void Write(JObject instance, List<Dictionary<string, object>> servers)
        {
            var path = FilePath(instance);
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                var writer = new NbtWriter(stream);
                writer.Byte(10); writer.String("");
                writer.Byte(9); writer.String("servers"); writer.Byte(10); writer.Int(servers.Count);
                foreach (var server in servers)
                {
                    writer.Byte(8); writer.String("name"); writer.String(server.TryGetValue("name", out var name) ? name?.ToString() ?? string.Empty : string.Empty);
                    writer.Byte(8); writer.String("ip"); writer.String(server.TryGetValue("ip", out var ip) ? ip?.ToString() ?? string.Empty : string.Empty);
                    var icon = server.TryGetValue("icon", out var rawIcon) ? rawIcon?.ToString() ?? string.Empty : string.Empty;
                    if (!string.IsNullOrEmpty(icon)) { writer.Byte(8); writer.String("icon"); writer.String(icon); }
                    writer.Byte(1); writer.String("acceptTextures"); writer.Byte(1);
                    writer.Byte(0);
                }
                writer.Byte(0);
            }
        }

        private static List<Dictionary<string, object>> Raw(JObject instance)
        {
            var root = Read(FilePath(instance));
            return root.TryGetValue("servers", out var raw) && raw is List<object> list
                ? list.OfType<Dictionary<string, object>>().Select(item => new Dictionary<string, object>(item)).ToList()
                : new List<Dictionary<string, object>>();
        }

        public static JArray Add(JObject instance, string name, string ip)
        {
            var address = (ip ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(address)) throw new InvalidOperationException("请输入服务器地址");
            if (address.Length > 255) throw new InvalidOperationException("服务器地址过长");
            var servers = Raw(instance);
            if (servers.Any(server => string.Equals(server.TryGetValue("ip", out var value) ? value?.ToString() : null, address, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("这个服务器已经在列表里了");
            servers.Add(new Dictionary<string, object>
            {
                ["name"] = string.IsNullOrWhiteSpace(name) ? address : name.Trim(),
                ["ip"] = address,
                ["icon"] = string.Empty
            });
            Write(instance, servers);
            return Entries(instance);
        }

        public static JArray Remove(JObject instance, int index)
        {
            var servers = Raw(instance);
            if (index < 0 || index >= servers.Count) throw new InvalidOperationException("服务器不存在");
            servers.RemoveAt(index);
            Write(instance, servers);
            return Entries(instance);
        }

        public static void Reveal(JObject instance)
        {
            var path = FilePath(instance);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
        }
    }
}
