using Tama.Core;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tama.Services;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Tama;

/// <summary>
/// 本机回环 HTTP bridge：供浏览器扩展的 native messaging host 调用（Tama 未在 UI 里时也能被调起）。
///
/// 安全模型（回环流量不出网卡，威胁不是窃听而是"谁来连"）：
///   - 只绑定 127.0.0.1 随机端口（HttpListener 端口 0 = 系统分配）
///   - 启动时生成 32B 随机 token，连同端口写入 %LOCALAPPDATA%\Tama\bridge.port（仅当前用户可读）
///   - 每个请求必须带 Authorization: Bearer &lt;token&gt;，常量时间比较，否则 403
///   - 恶意网页/本机进程拿不到 token（浏览器 SOP + 文件 ACL），无法调用
/// 用 token 而非自签 HTTPS：回环无网络路径攻击者，加密无收益；token 才能防"直接连端口"。
/// </summary>
public class LocalBridgeServer
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly string _portFilePath;
    private HttpListener? _listener;
    private string _token = string.Empty;
    private static readonly ILogger Log = Serilog.Log.ForContext<LocalBridgeServer>();

    public LocalBridgeServer(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
        _portFilePath = AppPaths.BridgePortPath;
    }

    /// <summary>启动回环监听（fire-and-forget；失败仅记日志，不影响主应用）。</summary>
    public async Task StartAsync()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);

            _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

            // 探测一个空闲回环端口（HttpListener 无 LocalEndpoint，绑 0 拿不到实际端口）
            int port;
            using (var probe = new TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                port = ((IPEndPoint)probe.LocalEndpoint).Port;
            }

            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();

            var portInfo = JsonSerializer.Serialize(new { port, token = _token });
            await File.WriteAllTextAsync(_portFilePath, portInfo);
            Log.Information("Local bridge listening on 127.0.0.1:{Port}", port);

            while (_listener.IsListening)
            {
                var ctx = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleAsync(ctx));
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Local bridge failed to start (extension passkey bridge unavailable)");
        }
    }

    public void Stop()
    {
        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch { /* already stopped */ }
    }

    private async Task HandleAsync(HttpListenerContext ctx)
    {
        try
        {
            var request = ctx.Request;
            var response = ctx.Response;

            // 1) token 鉴权（常量时间比较）
            var auth = request.Headers["Authorization"];
            var tokenOk = auth != null && auth.StartsWith("Bearer ", StringComparison.Ordinal)
                          && CryptographicOperations.FixedTimeEquals(
                              Encoding.UTF8.GetBytes(auth[7..]),
                              Encoding.UTF8.GetBytes(_token));
            if (!tokenOk)
            {
                response.StatusCode = 403;
                await WriteJsonAsync(response, JsonSerializer.Serialize(new { error = "forbidden" }));
                return;
            }

            // 2) 只允许 POST /api/bridge
            if (!string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(request.Url?.AbsolutePath, "/api/bridge", StringComparison.OrdinalIgnoreCase))
            {
                response.StatusCode = 404;
                await WriteJsonAsync(response, JsonSerializer.Serialize(new { error = "not found" }));
                return;
            }

            // 3) 读取 {method, params} 并交给扩展桥（与 Blazor Server 宿主的 BridgeController 同一实现）
            string body;
            using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync();
            }

            // 注意：扩展/native host 发的是小写键（method/params），默认反序列化大小写敏感会解析失败 → 400
            var req = JsonSerializer.Deserialize<BridgeHttpRequest>(body,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (req == null || string.IsNullOrEmpty(req.Method))
            {
                response.StatusCode = 400;
                await WriteJsonAsync(response, JsonSerializer.Serialize(new { error = "method required" }));
                return;
            }

            string result;
            using (var scope = _scopeFactory.CreateScope())
            {
                var bridge = scope.ServiceProvider.GetRequiredService<Tama.Services.Bridge.ExtensionBridge>();
                result = await bridge.HandleAsync(req.Method, req.Params);
            }

            response.StatusCode = 200;
            response.ContentType = "application/json; charset=utf-8";
            await WriteJsonAsync(response, result);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Local bridge request failed");
            try
            {
                ctx.Response.StatusCode = 500;
                await WriteJsonAsync(ctx.Response, JsonSerializer.Serialize(new { error = ex.Message }));
            }
            catch { /* client gone */ }
        }
        finally
        {
            try { ctx.Response.Close(); } catch { }
        }
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        response.ContentType = "application/json; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes);
    }

    private sealed class BridgeHttpRequest
    {
        public string? Method { get; set; }
        public string? Params { get; set; }
    }
}
