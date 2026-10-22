using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace HalfMcLauncher
{
    /// <summary>
    /// Minecraft Server List Ping（1.7+）：读取在线人数、版本、MOTD 与延迟。
    /// </summary>
    internal static class ServerPing
    {
        private const int Protocol = 769;

        private static void WriteVarInt(Stream stream, int value)
        {
            while (true)
            {
                var temp = (byte)(value & 0x7F);
                value = (int)((uint)value >> 7);
                if (value != 0) temp |= 0x80;
                stream.WriteByte(temp);
                if (value == 0) break;
            }
        }

        private static int ReadVarInt(Stream stream)
        {
            var value = 0;
            var position = 0;
            while (true)
            {
                var current = stream.ReadByte();
                if (current < 0) throw new EndOfStreamException();
                value |= (current & 0x7F) << position;
                if ((current & 0x80) == 0) break;
                position += 7;
                if (position > 35) throw new InvalidDataException("VarInt 过大");
            }
            return value;
        }

        private static byte[] Packet(params Action<MemoryStream>[] writers)
        {
            using (var body = new MemoryStream())
            {
                foreach (var write in writers) write(body);
                using (var packet = new MemoryStream())
                {
                    WriteVarInt(packet, (int)body.Length);
                    body.Position = 0;
                    body.CopyTo(packet);
                    return packet.ToArray();
                }
            }
        }

        private static string Flatten(JToken token)
        {
            if (token == null) return string.Empty;
            if (token.Type == JTokenType.String) return token.ToString();
            var text = token.Value<string>("text") ?? string.Empty;
            foreach (var extra in token["extra"] as JArray ?? new JArray()) text += Flatten(extra);
            return text;
        }

        public static async Task<JObject> PingAsync(string address, CancellationToken ct)
        {
            var target = (address ?? string.Empty).Trim();
            var host = target;
            var port = 25565;
            var colon = target.LastIndexOf(':');
            if (colon > 0 && colon < target.Length - 1 && target.IndexOf(':') == colon)
            {
                host = target.Substring(0, colon);
                int.TryParse(target.Substring(colon + 1), out port);
            }
            if (string.IsNullOrEmpty(host) || port <= 0 || port > 65535)
                throw new InvalidOperationException("服务器地址无效");

            var watch = Stopwatch.StartNew();
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(6));
                using (var client = new TcpClient())
                {
                    await client.ConnectAsync(host, port);
                    using (var stream = client.GetStream())
                    using (var linked = timeout.Token.Register(() => { try { client.Close(); } catch { } }))
                    {
                        stream.ReadTimeout = 6000;
                        var handshake = Packet(
                            body => WriteVarInt(body, 0x00),
                            body => WriteVarInt(body, Protocol),
                            body =>
                            {
                                var bytes = Encoding.UTF8.GetBytes(host);
                                WriteVarInt(body, bytes.Length);
                                body.Write(bytes, 0, bytes.Length);
                            },
                            body =>
                            {
                                body.WriteByte((byte)(port >> 8));
                                body.WriteByte((byte)port);
                            },
                            body => WriteVarInt(body, 1));
                        await stream.WriteAsync(handshake, 0, handshake.Length, timeout.Token).ConfigureAwait(false);
                        var request = Packet(body => WriteVarInt(body, 0x00));
                        await stream.WriteAsync(request, 0, request.Length, timeout.Token).ConfigureAwait(false);

                        var length = await Task.Run(() => ReadVarInt(stream), timeout.Token).ConfigureAwait(false);
                        if (length <= 0 || length > 1048576) throw new InvalidDataException("响应长度异常");
                        var payload = new byte[length];
                        var offset = 0;
                        while (offset < payload.Length)
                        {
                            var read = await stream.ReadAsync(payload, offset, payload.Length - offset, timeout.Token).ConfigureAwait(false);
                            if (read <= 0) throw new EndOfStreamException();
                            offset += read;
                        }
                        using (var body = new MemoryStream(payload))
                        {
                            var packetId = ReadVarInt(body);
                            if (packetId != 0x00) throw new InvalidDataException("响应类型异常");
                            var jsonLength = ReadVarInt(body);
                            var jsonBytes = new byte[jsonLength];
                            var jsonOffset = 0;
                            while (jsonOffset < jsonLength)
                            {
                                var read = body.Read(jsonBytes, jsonOffset, jsonLength - jsonOffset);
                                if (read <= 0) throw new EndOfStreamException();
                                jsonOffset += read;
                            }
                            var status = JObject.Parse(Encoding.UTF8.GetString(jsonBytes));
                            watch.Stop();
                            return new JObject
                            {
                                ["online"] = true,
                                ["latency"] = (int)watch.ElapsedMilliseconds,
                                ["players"] = status["players"]?.Value<int?>("online") ?? 0,
                                ["max"] = status["players"]?.Value<int?>("max") ?? 0,
                                ["version"] = status["version"]?.Value<string>("name") ?? string.Empty,
                                ["motd"] = System.Text.RegularExpressions.Regex.Replace(Flatten(status["description"]), "§.", string.Empty).Trim()
                            };
                        }
                    }
                }
            }
        }
    }
}
