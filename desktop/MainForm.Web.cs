using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace HalfMcLauncher
{
    internal sealed partial class MainForm
    {
        private async System.Threading.Tasks.Task InitializeBrowserAsync()
        {
            web = new WebView2 { Dock = System.Windows.Forms.DockStyle.Fill, DefaultBackgroundColor = System.Drawing.Color.FromArgb(14, 17, 16) };
            Controls.Add(web);
            web.CoreWebView2InitializationCompleted += OnBrowserReady;
            var options = new CoreWebView2EnvironmentOptions();
            var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(AppPaths.DataDir, "webview2"), options);
            await web.EnsureCoreWebView2Async(environment);
        }

        private void OnBrowserReady(object sender, CoreWebView2InitializationCompletedEventArgs e)
        {
            Diagnostics.Write("browser ready success=" + e.IsSuccess + " " + e.InitializationException?.Message);
            if (!e.IsSuccess)
            {
                System.Windows.Forms.MessageBox.Show("WebView2 初始化失败：" + e.InitializationException?.Message, "Half 启动失败");
                Close();
                return;
            }

            var core = web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.WebMessageReceived += OnWebMessage;
            core.AddScriptToExecuteOnDocumentCreatedAsync(BridgeScript);
            core.SetVirtualHostNameToFolderMapping("app.local", AppPaths.WebRoot, CoreWebView2HostResourceAccessKind.DenyCors);
            core.Navigate("https://app.local/index.html");
        }

        private async void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            JObject message;
            try { message = JObject.Parse(e.WebMessageAsJson); }
            catch { return; }

            var type = message.Value<string>("type");
            if (type == "window.drag") { StartDrag(); return; }
            if (type == "window.resize") { StartResize(message.Value<string>("edge")); return; }

            var id = message["id"];
            if (id == null) return;
            var method = message.Value<string>("method") ?? string.Empty;

            try
            {
                var value = await HostApi.HandleAsync(method, message["payload"]);
                Post(new JObject { ["id"] = id, ["ok"] = true, ["value"] = value ?? JValue.CreateNull() });
            }
            catch (Exception error)
            {
                Post(new JObject { ["id"] = id, ["ok"] = false, ["error"] = error.Message });
            }
        }

        private void Post(JObject payload)
        {
            if (web.CoreWebView2 == null) return;
            web.CoreWebView2.PostWebMessageAsJson(payload.ToString(Formatting.None));
        }

        internal void SendEvent(string type, object data)
        {
            // 进程输出、退出事件在后台线程触发，WebView2 只允许在 UI 线程调用。
            if (InvokeRequired)
            {
                try { BeginInvoke(new Action(() => SendEvent(type, data))); } catch (InvalidOperationException) { }
                return;
            }
            if (IsDisposed || web?.CoreWebView2 == null) return;
            Post(new JObject { ["type"] = type, ["data"] = data == null ? JValue.CreateNull() : JToken.FromObject(data) });
        }

        private const string BridgeScript = @"
(function () {
  if (window.launcher) return;
  var pending = new Map();
  var listeners = new Set();
  var seq = 0;
  window.chrome.webview.addEventListener('message', function (event) {
    var msg = event.data;
    if (!msg || typeof msg !== 'object') return;
    if (msg.id !== undefined && pending.has(msg.id)) {
      var entry = pending.get(msg.id); pending.delete(msg.id);
      if (msg.ok) entry.resolve(msg.value); else entry.reject(new Error(msg.error || '未知错误'));
      return;
    }
    if (msg.type) listeners.forEach(function (listener) { try { listener({ type: msg.type, data: msg.data }); } catch (error) { console.error(error); } });
  });
  window.launcher = {
    invoke: function (method, payload) {
      return new Promise(function (resolve, reject) {
        var id = ++seq; pending.set(id, { resolve: resolve, reject: reject });
        window.chrome.webview.postMessage({ id: id, method: method, payload: payload === undefined ? null : payload });
      });
    },
    onEvent: function (listener) { listeners.add(listener); return function () { listeners.delete(listener); }; }
  };
  document.addEventListener('mousedown', function (event) {
    if (event.button !== 0) return;
    var bar = event.target.closest && event.target.closest('.titlebar');
    if (bar && !(event.target.closest && event.target.closest('button, input, a, .window-controls'))) {
      window.chrome.webview.postMessage({ type: 'window.drag' }); return;
    }
    if (event.target.closest && event.target.closest('.modal, .command-modal, .dropdown')) return;
    var edge = '';
    var size = 6, x = event.clientX, y = event.clientY, w = window.innerWidth, h = window.innerHeight;
    if (y <= size) edge = x <= size ? 'topleft' : x >= w - size ? 'topright' : 'top';
    else if (y >= h - size) edge = x <= size ? 'bottomleft' : x >= w - size ? 'bottomright' : 'bottom';
    else if (x <= size) edge = 'left';
    else if (x >= w - size) edge = 'right';
    if (edge) window.chrome.webview.postMessage({ type: 'window.resize', edge: edge });
  }, true);
  document.documentElement.dataset.desktop = 'webview2';
})();
";
    }
}
