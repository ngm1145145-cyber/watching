using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Watching.Common;

namespace Watching.Server;

/// <summary>
/// 内置 HTTP + WebSocket 服务器（零第三方依赖）。
///   GET /                    手机 / 通用网页客户端
///   GET /mobile/…            手机客户端静态资源
///   GET /pc/…                电脑网页客户端静态资源
///   GET /api/info            设备信息（JSON）
///   GET /api/snapshot        当前画面截图（JPEG）
///   GET /health              健康检查
///   WS  /ws                  画面流
/// </summary>
public sealed class NetServer : IDisposable
{
    private readonly AppConfig _config;
    private readonly CaptureHub _hub;
    private readonly ServerHost _host;
    private TcpListener _listener;
    private Thread _acceptThread;
    private volatile bool _running;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public event Action<ClientConnection> ClientConnected;

    public NetServer(AppConfig config, CaptureHub hub, ServerHost host)
    {
        _config = config;
        _hub = hub;
        _host = host;
    }

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Any, _config.Port);
        _listener.Start(64);
        _running = true;
        _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "watching-accept" };
        _acceptThread.Start();
    }

    private void AcceptLoop()
    {
        while (_running)
        {
            try
            {
                var tcp = _listener.AcceptTcpClient();
                TuneSocket(tcp);

                // 记一笔：排查「手机连不上」时，这一行能区分
                // 「包根本没到本机」和「到了但被拒绝」
                try
                {
                    var remote = tcp.Client.RemoteEndPoint as IPEndPoint;
                    if (remote != null)
                    {
                        NetworkActivity.Record("TCP连接", remote.Address.ToString(), "端口 " + remote.Port);
                        if (!IPAddress.IsLoopback(remote.Address))
                            Log.Write($"收到局域网 TCP 连接：{remote.Address}:{remote.Port}");
                    }
                }
                catch { }

                var t = new Thread(() => HandleTcp(tcp)) { IsBackground = true };
                t.Start();
            }
            catch (Exception ex)
            {
                if (_running) Log.Error("接受连接失败", ex);
                Thread.Sleep(200);
            }
        }
    }

    /// <summary>
    /// 针对「连续推画面」这种场景调 socket：
    ///   NoDelay      —— 关掉 Nagle，单帧立刻发出去，少几十毫秒延迟
    ///   发送缓冲区   —— 调到 256KB，避免突发大帧时阻塞发送线程
    ///   接收缓冲区   —— 64KB 足够（客户端只发小的控制消息）
    ///   KeepAlive    —— 客户端异常掉线（比如手机断 WiFi）能被及时发现
    /// </summary>
    private static void TuneSocket(TcpClient tcp)
    {
        try
        {
            tcp.NoDelay = true;
            tcp.SendBufferSize = 256 * 1024;
            tcp.ReceiveBufferSize = 64 * 1024;
            tcp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        }
        catch (Exception ex)
        {
            Log.Write("调整 socket 参数失败（不影响功能）：" + ex.Message);
        }
    }

    // ---------------- 请求解析 ----------------

    private sealed class HttpRequestInfo
    {
        public string Method;
        public string Path;
        public string Query;
        public string Version;
        public Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
        public string WebSocketKey;
        public string Authorization;

        public bool IsWebSocket => !string.IsNullOrEmpty(WebSocketKey);
        public bool WantsGzip => Headers.TryGetValue("Accept-Encoding", out var v) &&
                                 v.Contains("gzip", StringComparison.OrdinalIgnoreCase);

        public string QueryValue(string key)
        {
            if (string.IsNullOrEmpty(Query)) return null;
            foreach (var part in Query.Split('&'))
            {
                int eq = part.IndexOf('=');
                if (eq < 0) continue;
                if (string.Equals(part.Substring(0, eq), key, StringComparison.OrdinalIgnoreCase))
                    return Uri.UnescapeDataString(part.Substring(eq + 1));
            }
            return null;
        }

        public string BasicPassword()
        {
            if (string.IsNullOrEmpty(Authorization)) return null;
            if (!Authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return null;
            try
            {
                var raw = Encoding.UTF8.GetString(Convert.FromBase64String(Authorization.Substring(6).Trim()));
                int c = raw.IndexOf(':');
                return c >= 0 ? raw.Substring(c + 1) : raw;
            }
            catch { return null; }
        }
    }

    private static async Task<HttpRequestInfo> ReadRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        int total = 0;
        int headerEnd = -1;

        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
            if (n <= 0) return null;
            total += n;
            headerEnd = FindHeaderEnd(buffer, total);
            if (headerEnd >= 0) break;
        }

        if (headerEnd < 0) return null;

        var text = Encoding.UTF8.GetString(buffer, 0, headerEnd);
        var lines = text.Split(new[] { "\r\n" }, StringSplitOptions.None);
        if (lines.Length == 0) return null;

        var first = lines[0].Split(' ');
        if (first.Length < 2) return null;

        var req = new HttpRequestInfo { Method = first[0].ToUpperInvariant(), Version = first.Length > 2 ? first[2] : "HTTP/1.1" };

        string target = first[1];
        int q = target.IndexOf('?');
        if (q >= 0)
        {
            req.Path = target.Substring(0, q);
            req.Query = target.Substring(q + 1);
        }
        else
        {
            req.Path = target;
        }

        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line)) continue;
            int c = line.IndexOf(':');
            if (c <= 0) continue;
            var name = line.Substring(0, c).Trim();
            var value = line.Substring(c + 1).Trim();
            req.Headers[name] = value;
        }

        req.Headers.TryGetValue("Sec-WebSocket-Key", out var key);
        req.WebSocketKey = key;
        req.Headers.TryGetValue("Authorization", out var auth);
        req.Authorization = auth;
        return req;
    }

    private static int FindHeaderEnd(byte[] buf, int len)
    {
        for (int i = 0; i + 3 < len; i++)
        {
            if (buf[i] == 13 && buf[i + 1] == 10 && buf[i + 2] == 13 && buf[i + 3] == 10)
                return i;
        }
        return -1;
    }

    // ---------------- 处理 ----------------

    private void HandleTcp(TcpClient tcp)
    {
        NetworkStream stream = null;
        try
        {
            stream = tcp.GetStream();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            var req = ReadRequestAsync(stream, cts.Token).GetAwaiter().GetResult();
            if (req == null) { tcp.Close(); return; }

            if (req.IsWebSocket)
            {
                if (!Authorized(req))
                {
                    WriteHttpResponse(stream, 401, "Unauthorized", "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes("需要访问密码"), headOnly: false);
                    tcp.Close();
                    return;
                }
                HandleWebSocket(tcp, stream, req);
                return;
            }

            HandleHttp(tcp, stream, req);
        }
        catch (Exception)
        {
            try { tcp.Close(); } catch { }
        }
    }

    private bool Authorized(HttpRequestInfo req)
    {
        if (!_config.AccessControlActive) return true;
        var pwd = req.BasicPassword() ?? req.QueryValue("pwd");
        return _config.CheckAccessPassword(pwd);
    }

    private void HandleWebSocket(TcpClient tcp, NetworkStream stream, HttpRequestInfo req)
    {
        var accept = WebSocketProtocol.ComputeAccept(req.WebSocketKey);
        var sb = new StringBuilder();
        sb.Append("HTTP/1.1 101 Switching Protocols\r\n");
        sb.Append("Upgrade: websocket\r\n");
        sb.Append("Connection: Upgrade\r\n");
        sb.Append("Sec-WebSocket-Accept: ").Append(accept).Append("\r\n");
        sb.Append("\r\n");
        var bytes = Encoding.ASCII.GetBytes(sb.ToString());
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush();

        var client = new ClientConnection(tcp, stream, _hub, _config, _host);
        _host.RegisterClient(client);
        ClientConnected?.Invoke(client);
        client.Start();
    }

    private void HandleHttp(TcpClient tcp, NetworkStream stream, HttpRequestInfo req)
    {
        try
        {
            if (req.Method != "GET" && req.Method != "HEAD")
            {
                WriteHttpResponse(stream, 405, "Method Not Allowed", "text/plain; charset=utf-8",
                    Encoding.UTF8.GetBytes("只支持 GET"));
                return;
            }

            string path = (req.Path ?? "/").Split('?')[0];
            if (path.Length == 0) path = "/";

            switch (path)
            {
                case "/":
                case "/index.html":
                case "/mobile":
                case "/mobile/":
                    if (!Authorized(req)) { Unauthorized(stream); return; }
                    ServeResource(stream, req, "web/mobile/index.html", "text/html; charset=utf-8");
                    return;

                case "/pc":
                case "/pc/":
                    if (!Authorized(req)) { Unauthorized(stream); return; }
                    ServeResource(stream, req, "web/pc/index.html", "text/html; charset=utf-8");
                    return;

                case "/health":
                    WriteHttpResponse(stream, 200, "OK", "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes("ok"));
                    return;

                case "/api/info":
                    if (!Authorized(req)) { Unauthorized(stream); return; }
                    WriteHttpResponse(stream, 200, "OK", "application/json; charset=utf-8",
                        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(BuildInfo(), JsonOpts)));
                    return;

                case "/api/snapshot":
                    if (!Authorized(req)) { Unauthorized(stream); return; }
                    ServeSnapshot(stream);
                    return;

                case "/favicon.ico":
                    WriteHttpResponse(stream, 204, "No Content", "image/x-icon", Array.Empty<byte>());
                    return;
            }

            if (path.StartsWith("/mobile/", StringComparison.OrdinalIgnoreCase))
            {
                if (!Authorized(req)) { Unauthorized(stream); return; }
                var rel = path.Substring("/mobile/".Length);
                var logical = "web/mobile/" + rel.Replace("..", "").TrimStart('/');
                ServeResource(stream, req, logical, MimeFor(rel));
                return;
            }

            if (path.StartsWith("/pc/", StringComparison.OrdinalIgnoreCase))
            {
                if (!Authorized(req)) { Unauthorized(stream); return; }
                var rel = path.Substring("/pc/".Length);
                var logical = "web/pc/" + rel.Replace("..", "").TrimStart('/');
                ServeResource(stream, req, logical, MimeFor(rel));
                return;
            }

            // 根路径下的静态资源（手机客户端页面用相对路径引用 style.css / app.js）
            if (!path.Contains(".."))
            {
                var rel = path.TrimStart('/');
                if (rel.Length > 0 && rel.IndexOf('/') < 0)
                {
                    var mobileLogical = "web/mobile/" + rel;
                    if (ResourceHelper.Read(mobileLogical) != null)
                    {
                        if (!Authorized(req)) { Unauthorized(stream); return; }
                        ServeResource(stream, req, mobileLogical, MimeFor(rel));
                        return;
                    }

                    var pcLogical = "web/pc/" + rel;
                    if (ResourceHelper.Read(pcLogical) != null)
                    {
                        if (!Authorized(req)) { Unauthorized(stream); return; }
                        ServeResource(stream, req, pcLogical, MimeFor(rel));
                        return;
                    }

                    // 共用的客户端脚本/样式（两端一致）
                    var sharedLogical = "web/" + rel;
                    if (ResourceHelper.Read(sharedLogical) != null)
                    {
                        if (!Authorized(req)) { Unauthorized(stream); return; }
                        ServeResource(stream, req, sharedLogical, MimeFor(rel));
                        return;
                    }
                }
            }

            if (path.StartsWith("/web/", StringComparison.OrdinalIgnoreCase))
            {
                var rel = path.Substring("/web/".Length);
                ServeResource(stream, req, "web/" + rel.Replace("..", ""), MimeFor(rel));
                return;
            }

            WriteHttpResponse(stream, 404, "Not Found", "text/plain; charset=utf-8",
                Encoding.UTF8.GetBytes("404"));
        }
        catch (Exception ex)
        {
            Log.Error("HTTP 处理失败", ex);
        }
        finally
        {
            try { tcp.Close(); } catch { }
        }
    }

    private void Unauthorized(NetworkStream stream)
    {
        var body = Encoding.UTF8.GetBytes("需要访问密码");
        var header = "HTTP/1.1 401 Unauthorized\r\n" +
                     "WWW-Authenticate: Basic realm=\"Watching\"\r\n" +
                     "Content-Type: text/plain; charset=utf-8\r\n" +
                     $"Content-Length: {body.Length}\r\n" +
                     "Connection: close\r\n\r\n";
        var hb = Encoding.ASCII.GetBytes(header);
        stream.Write(hb, 0, hb.Length);
        stream.Write(body, 0, body.Length);
    }

    private AppInfo BuildInfo()
    {
        return new AppInfo
        {
            MachineName = Environment.MachineName,
            UserName = Environment.UserName,
            OS = Environment.OSVersion.VersionString,
            Screen = $"{User32.GetSystemMetrics(User32.SM_CXVIRTUALSCREEN)}x{User32.GetSystemMetrics(User32.SM_CYVIRTUALSCREEN)}",
            RemoteControl = _host.RemoteControlEnabled,
            Clients = _host.ClientCount
        };
    }

    private void ServeSnapshot(NetworkStream stream)
    {
        var crop = Rectangle.Empty;
        int width = Math.Min(_config.MaxWidth <= 0 ? 1920 : _config.MaxWidth, 1920);
        var streamObj = _hub.Acquire(Math.Clamp(_config.Quality, 40, 85), 1, width, crop);
        try
        {
            byte[] jpeg = null;
            long start = Environment.TickCount64;
            while (Environment.TickCount64 - start < 5000)
            {
                if (streamObj.Engine.TryGetLatest(out jpeg, out _, out _, out _, out _) && jpeg != null) break;
                Thread.Sleep(60);
            }

            if (jpeg == null)
            {
                WriteHttpResponse(stream, 503, "Service Unavailable", "text/plain; charset=utf-8",
                    Encoding.UTF8.GetBytes("暂无画面"));
                return;
            }

            WriteHttpResponse(stream, 200, "OK", "image/jpeg", jpeg, noCache: true);
        }
        finally
        {
            _hub.Release(streamObj);
        }
    }

    private void ServeResource(NetworkStream stream, HttpRequestInfo req, string logicalPath, string contentType)
    {
        var bytes = ResourceHelper.Read(logicalPath);
        if (bytes == null)
        {
            WriteHttpResponse(stream, 404, "Not Found", "text/plain; charset=utf-8",
                Encoding.UTF8.GetBytes("找不到资源：" + logicalPath));
            return;
        }

        bool headOnly = req.Method == "HEAD";
        bool gzip = req.WantsGzip && IsText(contentType) && bytes.Length > 1024;
        byte[] body = bytes;
        if (gzip)
        {
            using var ms = new MemoryStream();
            using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionLevel.Fastest, true))
                gz.Write(bytes, 0, bytes.Length);
            body = ms.ToArray();
        }

        var header = new StringBuilder();
        header.Append("HTTP/1.1 200 OK\r\n");
        header.Append("Content-Type: ").Append(contentType).Append("\r\n");
        header.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        if (gzip) header.Append("Content-Encoding: gzip\r\n");
        header.Append("Cache-Control: no-store\r\n");
        header.Append("Connection: close\r\n");
        header.Append("\r\n");

        var hb = Encoding.ASCII.GetBytes(header.ToString());
        stream.Write(hb, 0, hb.Length);
        if (!headOnly && body.Length > 0) stream.Write(body, 0, body.Length);
    }

    private static bool IsText(string contentType)
        => contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
           contentType.Contains("json", StringComparison.OrdinalIgnoreCase) ||
           contentType.Contains("javascript", StringComparison.OrdinalIgnoreCase) ||
           contentType.Contains("svg", StringComparison.OrdinalIgnoreCase);

    private static string MimeFor(string path)
    {
        var ext = Path.GetExtension(path ?? "").ToLowerInvariant();
        return ext switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".js" or ".mjs" => "application/javascript; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".svg" => "image/svg+xml",
            ".ico" => "image/x-icon",
            ".woff2" => "font/woff2",
            ".txt" => "text/plain; charset=utf-8",
            _ => "application/octet-stream",
        };
    }

    private static void WriteHttpResponse(NetworkStream stream, int code, string reason, string contentType,
        byte[] body, bool headOnly = false, bool noCache = false)
    {
        body ??= Array.Empty<byte>();
        var header = new StringBuilder();
        header.Append("HTTP/1.1 ").Append(code).Append(' ').Append(reason).Append("\r\n");
        header.Append("Content-Type: ").Append(contentType).Append("\r\n");
        header.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        if (noCache) header.Append("Cache-Control: no-store\r\n");
        header.Append("Access-Control-Allow-Origin: *\r\n");
        header.Append("Connection: close\r\n");
        header.Append("\r\n");
        var hb = Encoding.ASCII.GetBytes(header.ToString());
        stream.Write(hb, 0, hb.Length);
        if (!headOnly && body.Length > 0) stream.Write(body, 0, body.Length);
    }

    public void Dispose()
    {
        _running = false;
        try { _listener?.Stop(); } catch { }
    }
}
