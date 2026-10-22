using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using Newtonsoft.Json.Linq;

namespace HalfMcLauncher
{
    /// <summary>
    /// Microsoft 账户登录与令牌管理。
    /// 支持两种流程：默认浏览器回环回调（PKCE）和设备代码；两者都只需要公共客户端 ID，
    /// 不保存任何用户密码，令牌用 DPAPI 加密存在 data/accounts.bin。
    /// </summary>
    internal static class MicrosoftAuth
    {
        internal const string DefaultClientId = "c36a9fb6-4f2a-41ff-90bd-ae7cc92031eb";
        private const string Authority = "https://login.microsoftonline.com/consumers/oauth2/v2.0/";
        private const string Scope = "XboxLive.signin offline_access";

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(40) };
        private static CancellationTokenSource pending;

        public static bool IsPending { get { return pending != null; } }

        /// <summary>最近一次登录要打开的页面；设备代码流程里还会保存代码，供界面重新打开/复制。</summary>
        public static string LastUrl { get; private set; }
        public static string LastCode { get; private set; }

        /// <summary>外部浏览器全部失败时的兜底：由外壳把页面加载到应用内的 WebView2。</summary>
        public static Func<string, bool> InAppOpener { get; set; }

        /// <summary>用户显式选择“在启动器内登录”时，跳过所有外部浏览器尝试。</summary>
        public static bool InAppOnly { get; set; }

        public static void Cancel()
        {
            var current = pending;
            if (current != null)
            {
                try { current.Cancel(); } catch { }
            }
        }

        /// <summary>设置里的应用 ID → data/client-id.txt → 内置公共 ID。</summary>
        public static string ClientId()
        {
            try
            {
                var configured = Program.MainWindow != null ? Program.MainWindow.Store.Settings.Value<string>("clientId") : null;
                if (ClientIdPattern.IsMatch(configured ?? string.Empty)) return configured.Trim();
            }
            catch { }
            try
            {
                var file = Path.Combine(AppPaths.DataDir, "client-id.txt");
                if (File.Exists(file))
                {
                    var text = File.ReadAllText(file).Trim();
                    if (ClientIdPattern.IsMatch(text)) return text;
                }
            }
            catch { }
            return DefaultClientId;
        }

        public static string ClientIdSource()
        {
            try
            {
                var configured = Program.MainWindow != null ? Program.MainWindow.Store.Settings.Value<string>("clientId") : null;
                if (ClientIdPattern.IsMatch(configured ?? string.Empty)) return "settings";
            }
            catch { }
            try
            {
                var file = Path.Combine(AppPaths.DataDir, "client-id.txt");
                if (File.Exists(file) && ClientIdPattern.IsMatch(File.ReadAllText(file).Trim())) return "file";
            }
            catch { }
            return "builtin";
        }

        private static readonly System.Text.RegularExpressions.Regex ClientIdPattern =
            new System.Text.RegularExpressions.Regex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>交互式登录，返回可直接写入 state.accounts 的账户对象。</summary>
        public static async Task<JObject> LoginAsync(string method, Action<string, JToken> emit)
        {
            if (pending != null) throw new InvalidOperationException("已有登录正在进行，请先完成或取消");
            var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            pending = cts;
            LastUrl = string.Empty;
            LastCode = string.Empty;
            var clientId = ClientId();
            try
            {
                emit("loginStage", new JObject { ["method"] = method, ["stage"] = method == "device" ? "正在获取设备代码" : "正在打开默认浏览器" });
                var token = method == "device"
                    ? await DeviceFlowAsync(clientId, emit, cts.Token)
                    : await BrowserFlowAsync(clientId, emit, cts.Token);

                cts.Token.ThrowIfCancellationRequested();
                emit("loginStage", new JObject { ["method"] = method, ["stage"] = "授权完成，正在验证 Xbox 与 Minecraft 档案" });
                var profile = await MinecraftTokenAsync(token.AccessToken, cts.Token);

                var id = Guid.NewGuid().ToString();
                SaveSecret(new Secret
                {
                    Id = id,
                    ClientId = clientId,
                    RefreshToken = token.RefreshToken ?? string.Empty,
                    AccessToken = profile.AccessToken,
                    ExpiresAt = profile.ExpiresAt
                });
                return new JObject
                {
                    ["id"] = id,
                    ["name"] = profile.Name,
                    ["uuid"] = profile.Uuid,
                    ["expiresAt"] = profile.ExpiresAt,
                    ["kind"] = "microsoft",
                    ["skin"] = profile.SkinUrl ?? string.Empty,
                    ["cape"] = profile.CapeUrl ?? string.Empty
                };
            }
            catch (Exception error)
            {
                Diagnostics.Write("auth login " + method + " failed: " + error);
                throw;
            }
            finally
            {
                pending = null;
                cts.Dispose();
                emit("loginFinished", null);
            }
        }

        /// <summary>取账户当前可用的 Minecraft 访问令牌，过期时用刷新令牌续期。</summary>
        public static async Task<string> SessionAsync(string accountId, CancellationToken ct)
        {
            var list = Secrets();
            var secret = list.FirstOrDefault(item => item.Id == accountId);
            if (secret == null) throw new InvalidOperationException("登录已失效，请重新添加微软账户");
            if (secret.ExpiresAt - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() < 120000)
            {
                var token = await RefreshAsync(secret.ClientId, secret.RefreshToken, ct);
                var profile = await MinecraftTokenAsync(token.AccessToken, ct);
                secret.RefreshToken = string.IsNullOrEmpty(token.RefreshToken) ? secret.RefreshToken : token.RefreshToken;
                secret.AccessToken = profile.AccessToken;
                secret.ExpiresAt = profile.ExpiresAt;
                WriteSecrets(list);
            }
            return secret.AccessToken;
        }

        public static void Remove(string accountId)
        {
            var list = Secrets();
            if (list.RemoveAll(item => item.Id == accountId) > 0) WriteSecrets(list);
        }

        public static void Reset()
        {
            try { File.Delete(SecretsFile); } catch { }
        }

        // ---------- 流程 ----------

        private static async Task<TokenResult> DeviceFlowAsync(string clientId, Action<string, JToken> emit, CancellationToken ct)
        {
            var start = await PostForm(Authority + "devicecode", new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["scope"] = Scope
            }, ct);
            var code = start.Value<string>("user_code");
            var url = start.Value<string>("verification_uri") ?? "https://microsoft.com/devicelogin";
            var message = start.Value<string>("message") ?? ("在浏览器打开 " + url + " 并输入代码 " + code);
            LastCode = code;
            LastUrl = url;
            emit("deviceCode", new JObject { ["code"] = code, ["url"] = url, ["message"] = message });
            if (!OpenInBrowser(url)) emit("loginStage", new JObject { ["method"] = "device", ["stage"] = "浏览器未能自动打开，请手动访问 " + url });

            var interval = Math.Max(2, start.Value<int?>("interval") ?? 5);
            var expires = DateTime.UtcNow.AddSeconds(start.Value<int?>("expires_in") ?? 900);
            while (DateTime.UtcNow < expires)
            {
                await Task.Delay(TimeSpan.FromSeconds(interval), ct);
                var reply = await PostForm(Authority + "token", new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                    ["client_id"] = clientId,
                    ["device_code"] = start.Value<string>("device_code") ?? string.Empty
                }, ct, true);
                var access = reply.Value<string>("access_token");
                if (!string.IsNullOrEmpty(access)) return new TokenResult { AccessToken = access, RefreshToken = reply.Value<string>("refresh_token") };
                var error = reply.Value<string>("error");
                if (error == "authorization_pending") continue;
                if (error == "slow_down") { interval += 5; continue; }
                if (error == "expired_token") throw new InvalidOperationException("设备代码已过期，请重新登录");
                if (error == "access_denied") throw new InvalidOperationException("你已取消 Microsoft 授权");
                throw new InvalidOperationException(ErrorMessage(reply));
            }
            throw new InvalidOperationException("登录超时，请重新尝试");
        }

        private static async Task<TokenResult> BrowserFlowAsync(string clientId, Action<string, JToken> emit, CancellationToken ct)
        {
            var state = RandomToken(32);
            var verifier = RandomToken(48);
            var challenge = Base64Url(SHA256.Create().ComputeHash(Encoding.ASCII.GetBytes(verifier)));

            TcpListener listener;
            try
            {
                listener = new TcpListener(IPAddress.IPv6Any, 0);
                listener.Server.DualMode = true;
                listener.Start();
            }
            catch
            {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
            }

            using (ct.Register(() => { try { listener.Stop(); } catch { } }))
            {
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                var redirect = "http://localhost:" + port;
                var url = Authority + "authorize?client_id=" + Uri.EscapeDataString(clientId)
                    + "&response_type=code&redirect_uri=" + Uri.EscapeDataString(redirect)
                    + "&response_mode=query&scope=" + Uri.EscapeDataString(Scope)
                    + "&state=" + Uri.EscapeDataString(state)
                    + "&code_challenge=" + Uri.EscapeDataString(challenge)
                    + "&code_challenge_method=S256&prompt=select_account";
                emit("loginStage", new JObject { ["method"] = "browser", ["stage"] = "请在浏览器完成授权，完成后会自动添加账户" });
                LastUrl = url;
                if (!OpenInBrowser(url)) emit("loginStage", new JObject { ["method"] = "browser", ["stage"] = "浏览器未能自动打开，请手动访问 " + url });

                string code;
                try
                {
                    code = await WaitForCodeAsync(listener, state, ct);
                }
                finally
                {
                    try { listener.Stop(); } catch { }
                }

                ct.ThrowIfCancellationRequested();
                var reply = await PostForm(Authority + "token", new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["client_id"] = clientId,
                    ["code"] = code,
                    ["redirect_uri"] = redirect,
                    ["code_verifier"] = verifier
                }, ct);
                var access = reply.Value<string>("access_token");
                if (string.IsNullOrEmpty(access)) throw new InvalidOperationException(ErrorMessage(reply));
                return new TokenResult { AccessToken = access, RefreshToken = reply.Value<string>("refresh_token") };
            }
        }

        private static async Task<string> WaitForCodeAsync(TcpListener listener, string expectedState, CancellationToken ct)
        {
            TcpClient client;
            try
            {
                client = await Task.Run(() => listener.AcceptTcpClient(), ct);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException(ct);
            }
            using (client)
            using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.ASCII, false, 2048, true))
            {
                var requestLine = await reader.ReadLineAsync() ?? string.Empty;
                string header;
                while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync())) { }
                var path = requestLine.Split(' ').Skip(1).FirstOrDefault() ?? "/";
                var query = path.Contains("?") ? path.Substring(path.IndexOf('?') + 1) : string.Empty;
                var values = ParseQuery(query);

                string code, state, error;
                values.TryGetValue("code", out code);
                values.TryGetValue("state", out state);
                values.TryGetValue("error", out error);
                Diagnostics.Write("auth callback state=" + string.Equals(state, expectedState, StringComparison.Ordinal)
                    + " code=" + !string.IsNullOrEmpty(code) + " error=" + (error ?? "none"));

                var body = string.IsNullOrEmpty(error)
                    ? "<!doctype html><meta charset=\"utf-8\"><title>授权完成</title><h1>授权已收到</h1><p>请返回 Half Mc Launcher，正在验证 Minecraft 账户。</p>"
                    : "<!doctype html><meta charset=\"utf-8\"><title>授权未完成</title><h1>授权未完成</h1><p>请返回启动器重试。</p>";
                var response = "HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + Encoding.UTF8.GetByteCount(body)
                    + "\r\nConnection: close\r\n\r\n" + body;
                var bytes = Encoding.UTF8.GetBytes(response);
                await stream.WriteAsync(bytes, 0, bytes.Length, ct);
                await stream.FlushAsync(ct);

                if (!string.Equals(state, expectedState, StringComparison.Ordinal)) throw new InvalidOperationException("授权状态无效，请重新登录");
                if (!string.IsNullOrEmpty(error)) throw new InvalidOperationException(error == "access_denied" ? "你已取消 Microsoft 授权" : "Microsoft 授权失败，请重新登录");
                if (string.IsNullOrEmpty(code)) throw new InvalidOperationException("未收到授权结果");
                return code;
            }
        }

        // ---------- Xbox / Minecraft ----------

        private static async Task<Profile> MinecraftTokenAsync(string msToken, CancellationToken ct)
        {
            var xbox = await PostJson("https://user.auth.xboxlive.com/user/authenticate", new JObject
            {
                ["Properties"] = new JObject { ["AuthMethod"] = "RPS", ["SiteName"] = "user.auth.xboxlive.com", ["RpsTicket"] = "d=" + msToken },
                ["RelyingParty"] = "http://auth.xboxlive.com",
                ["TokenType"] = "JWT"
            }, ct);
            var xsts = await PostJson("https://xsts.auth.xboxlive.com/xsts/authorize", new JObject
            {
                ["Properties"] = new JObject { ["SandboxId"] = "RETAIL", ["UserTokens"] = new JArray(xbox.Value<string>("Token")) },
                ["RelyingParty"] = "rp://api.minecraftservices.com/",
                ["TokenType"] = "JWT"
            }, ct);
            var uhs = xsts["DisplayClaims"]?["xui"]?.FirstOrDefault()?["uhs"]?.ToString();
            if (string.IsNullOrEmpty(uhs) || string.IsNullOrEmpty(xsts.Value<string>("Token")))
            {
                var xerr = xsts.Value<string>("XErr");
                if (xerr == "2148916233") throw new InvalidOperationException("此 Microsoft 账户还没有 Xbox 账户，请先在 Xbox 官网创建");
                if (xerr == "2148916238") throw new InvalidOperationException("此账户是未成年人账户，需要加入家庭组后才能登录");
                throw new InvalidOperationException("Xbox 身份验证没有返回有效凭据");
            }
            var mc = await PostJson("https://api.minecraftservices.com/authentication/login_with_xbox", new JObject
            {
                ["identityToken"] = "XBL3.0 x=" + uhs + ";" + xsts.Value<string>("Token")
            }, ct);
            var access = mc.Value<string>("access_token");
            if (string.IsNullOrEmpty(access)) throw new InvalidOperationException("Minecraft 未返回访问令牌");
            var profile = await ProfileFromTokenAsync(access, ct);
            profile.AccessToken = access;
            profile.ExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)(mc.Value<double?>("expires_in") ?? 86400) * 1000;
            return profile;
        }

        /// <summary>用已有 Minecraft 访问令牌读取档案（名字、皮肤、披风）。</summary>
        public static async Task<Profile> ProfileFromTokenAsync(string access, CancellationToken ct)
        {
            var request = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + access);
            using (var response = await Http.SendAsync(request, ct))
            {
                var text = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException("此账户没有可用的 Minecraft Java 档案（请确认已购买游戏）");
                var profile = JObject.Parse(text);
                var uuid = profile.Value<string>("id");
                var name = profile.Value<string>("name");
                if (string.IsNullOrEmpty(uuid) || string.IsNullOrEmpty(name)) throw new InvalidOperationException("此账户未拥有可用的 Minecraft Java 档案");
                return new Profile
                {
                    Uuid = uuid,
                    Name = name,
                    SkinUrl = SecureUrl((profile["skins"] as JArray)?.FirstOrDefault()?.Value<string>("url")),
                    CapeUrl = SecureUrl((profile["capes"] as JArray)?.FirstOrDefault()?.Value<string>("url"))
                };
            }
        }

        /// <summary>刷新账户资料：名字、皮肤、披风。令牌失效时会自动续期。</summary>
        public static async Task<JObject> RefreshAccountAsync(string accountId, CancellationToken ct)
        {
            var access = await SessionAsync(accountId, ct);
            var profile = await ProfileFromTokenAsync(access, ct);
            return new JObject
            {
                ["name"] = profile.Name,
                ["uuid"] = profile.Uuid,
                ["skin"] = profile.SkinUrl ?? string.Empty,
                ["cape"] = profile.CapeUrl ?? string.Empty
            };
        }

        /// <summary>Mojang 的皮肤链接常是 http，页面在 https 下会被拦截，统一升级到 https。</summary>
        private static string SecureUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) return url;
            return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                ? "https://" + url.Substring(7)
                : url;
        }

        private static async Task<TokenResult> RefreshAsync(string clientId, string refreshToken, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(refreshToken)) throw new InvalidOperationException("登录已失效，请重新添加微软账户");
            var reply = await PostForm(Authority + "token", new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = clientId,
                ["refresh_token"] = refreshToken,
                ["scope"] = Scope
            }, ct, true);
            var access = reply.Value<string>("access_token");
            if (string.IsNullOrEmpty(access)) throw new InvalidOperationException(ErrorMessage(reply));
            return new TokenResult { AccessToken = access, RefreshToken = reply.Value<string>("refresh_token") };
        }

        // ---------- 存储 ----------

        private static string SecretsFile { get { return Path.Combine(AppPaths.DataDir, "accounts.bin"); } }

        private sealed class Secret
        {
            public string Id { get; set; }
            public string ClientId { get; set; }
            public string RefreshToken { get; set; }
            public string AccessToken { get; set; }
            public long ExpiresAt { get; set; }
        }

        private static List<Secret> Secrets()
        {
            try
            {
                if (!File.Exists(SecretsFile)) return new List<Secret>();
                var bytes = ProtectedData.Unprotect(File.ReadAllBytes(SecretsFile), null, DataProtectionScope.CurrentUser);
                return Newtonsoft.Json.JsonConvert.DeserializeObject<List<Secret>>(Encoding.UTF8.GetString(bytes)) ?? new List<Secret>();
            }
            catch
            {
                return new List<Secret>();
            }
        }

        private static void WriteSecrets(List<Secret> list)
        {
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(list);
            var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(AppPaths.DataDir);
            var temp = SecretsFile + ".tmp";
            File.WriteAllBytes(temp, bytes);
            if (File.Exists(SecretsFile)) File.Delete(SecretsFile);
            File.Move(temp, SecretsFile);
        }

        private static void SaveSecret(Secret secret)
        {
            var list = Secrets();
            list.RemoveAll(item => item.Id == secret.Id);
            list.Add(secret);
            WriteSecrets(list);
        }

        // ---------- 工具 ----------

        private sealed class TokenResult
        {
            public string AccessToken { get; set; }
            public string RefreshToken { get; set; }
        }

        internal sealed class Profile
        {
            public string Uuid { get; set; }
            public string Name { get; set; }
            public string AccessToken { get; set; }
            public long ExpiresAt { get; set; }
            public string SkinUrl { get; set; }
            public string CapeUrl { get; set; }
        }

        private static string RandomToken(int bytes)
        {
            var buffer = new byte[bytes];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(buffer);
            return Base64Url(buffer);
        }

        private static string Base64Url(byte[] bytes)
        {
            return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in (query ?? string.Empty).Split('&'))
            {
                if (pair.Length == 0) continue;
                var index = pair.IndexOf('=');
                var key = index < 0 ? pair : pair.Substring(0, index);
                var value = index < 0 ? string.Empty : pair.Substring(index + 1);
                result[Uri.UnescapeDataString(key)] = Uri.UnescapeDataString(value.Replace('+', ' '));
            }
            return result;
        }

        /// <summary>用系统默认浏览器打开链接：先按注册表里的默认浏览器命令直接启动，再退回 ShellExecute。</summary>
        public static bool OpenInBrowser(string url)
        {
            if (string.IsNullOrEmpty(url)) return false;
            if (InAppOnly && InAppOpener != null && InAppOpener(url))
            {
                Diagnostics.Write("auth open in-app (explicit) " + url);
                return true;
            }
            if (LaunchDefaultBrowser(url)) return true;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                Diagnostics.Write("auth open via shell " + url);
                return true;
            }
            catch (Exception error) { Diagnostics.Write("auth open via shell failed: " + error.Message); }
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c start \"\" \"" + url + "\"") { UseShellExecute = false, CreateNoWindow = true });
                Diagnostics.Write("auth open via cmd " + url);
                return true;
            }
            catch (Exception error) { Diagnostics.Write("auth open via cmd failed: " + error.Message); }
            if (InAppOpener != null && InAppOpener(url))
            {
                Diagnostics.Write("auth open in-app " + url);
                return true;
            }
            return false;
        }

        private static bool OpenViaExplorer(string url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", "\"" + url + "\"") { UseShellExecute = true });
                Diagnostics.Write("auth open via explorer " + url);
                return true;
            }
            catch (Exception error)
            {
                Diagnostics.Write("auth open via explorer failed: " + error.Message);
                return false;
            }
        }

        private static bool IsElevated()
        {
            try
            {
                using (var identity = System.Security.Principal.WindowsIdentity.GetCurrent())
                    return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
            }
            catch { return false; }
        }

        private static bool LaunchDefaultBrowser(string url)
        {
            foreach (var scheme in new[] { "http", "https" })
            {
                string progId = null;
                try
                {
                    progId = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\Shell\Associations\UrlAssociations\" + scheme + @"\UserChoice", "ProgId", null) as string;
                }
                catch { }
                if (string.IsNullOrEmpty(progId)) continue;
                string command = null;
                try { command = Registry.GetValue(@"HKEY_CLASSES_ROOT\" + progId + @"\shell\open\command", "", null) as string; }
                catch { }
                if (string.IsNullOrEmpty(command)) continue;
                if (RunBrowserCommand(command, url))
                {
                    Diagnostics.Write("auth open ok (default browser " + progId + ") " + url);
                    return true;
                }
            }
            return false;
        }

        private static bool RunBrowserCommand(string command, string url)
        {
            try
            {
                var expanded = Environment.ExpandEnvironmentVariables(command).Trim();
                string exe, args;
                if (expanded.StartsWith("\"", StringComparison.Ordinal))
                {
                    var end = expanded.IndexOf('"', 1);
                    if (end < 0) return false;
                    exe = expanded.Substring(1, end - 1);
                    args = expanded.Substring(end + 1).Trim();
                }
                else
                {
                    var space = expanded.IndexOf(' ');
                    exe = space < 0 ? expanded : expanded.Substring(0, space);
                    args = space < 0 ? string.Empty : expanded.Substring(space + 1).Trim();
                }
                if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return false;
                // 占位符可能已经被引号包住（例如 "%1"），这里只替换成裸链接，避免出现双引号。
                if (args.Contains("%1")) args = args.Replace("%1", url);
                else if (args.Contains("%L")) args = args.Replace("%L", url);
                else if (args.Contains("%*")) args = args.Replace("%*", url);
                else args = (args + " \"" + url + "\"").Trim();
                var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exe, args) { UseShellExecute = false });
                if (process == null) return false;
                try
                {
                    // 浏览器已在运行时，入口进程会立刻以 0 退出；非 0 通常说明配置锁或权限失败。
                    if (process.WaitForExit(1500) && process.ExitCode != 0)
                    {
                        Diagnostics.Write("default browser exited with " + process.ExitCode + ": " + exe);
                        return false;
                    }
                }
                catch { }
                return true;
            }
            catch (Exception error)
            {
                Diagnostics.Write("default browser launch failed: " + error.Message);
                return false;
            }
        }

        private static string ErrorMessage(JObject reply)
        {
            var description = reply.Value<string>("error_description");
            if (!string.IsNullOrEmpty(description)) return description.Split('\n')[0];
            var error = reply.Value<string>("error");
            return string.IsNullOrEmpty(error) ? "Microsoft 登录失败，请重试" : "Microsoft 登录失败：" + error;
        }

        private static async Task<JObject> PostForm(string url, Dictionary<string, string> form, CancellationToken ct, bool allowError = false)
        {
            using (var content = new FormUrlEncodedContent(form))
            using (var response = await Http.PostAsync(url, content, ct))
            {
                var text = await response.Content.ReadAsStringAsync();
                var reply = Parse(text);
                if (!response.IsSuccessStatusCode && !allowError) throw new InvalidOperationException(ErrorMessage(reply));
                return reply;
            }
        }

        private static async Task<JObject> PostJson(string url, JObject body, CancellationToken ct)
        {
            using (var content = new StringContent(body.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json"))
            using (var response = await Http.PostAsync(url, content, ct))
            {
                var text = await response.Content.ReadAsStringAsync();
                var reply = Parse(text);
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ErrorMessage(reply));
                return reply;
            }
        }

        private static JObject Parse(string text)
        {
            try { return JObject.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text); }
            catch { return new JObject(); }
        }
    }
}
