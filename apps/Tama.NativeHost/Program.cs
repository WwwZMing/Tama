using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Tama.Core;

namespace Tama.NativeHost;

/// <summary>
/// Chrome/Edge native messaging host：把浏览器扩展的请求转发到 Tama 主进程的本机回环 bridge。
///
/// 链路：扩展 → native messaging（stdin/stdout，Chrome 系统级通道）→ 本进程 → HTTP 127.0.0.1:port（Bearer token）→ Tama
///
/// 协议（与 Chrome 约定一致）：
///   每条消息 = 4 字节小端长度 + UTF-8 JSON
///   消息体 = { "method": "webauthn/create", "params": "<json string|null>" }（与 bridge 的 HTTP 路由一致）
/// </summary>
public static class Program
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var stdin = Console.OpenStandardInput();
            var stdout = Console.OpenStandardOutput();
            var stdinBuffer = new byte[4];

            while (true)
            {
                // 读长度（EOF 即扩展断开，正常退出）
                var lenRead = await ReadExactAsync(stdin, stdinBuffer, 4);
                if (lenRead < 4) break;
                var msgLen = BitConverter.ToInt32(stdinBuffer, 0);
                if (msgLen < 0 || msgLen > 16 * 1024 * 1024) break; // 上限 16MB，防畸形

                var msgBytes = new byte[msgLen];
                if (await ReadExactAsync(stdin, msgBytes, msgLen) < msgLen) break;

                var requestJson = Encoding.UTF8.GetString(msgBytes);
                var responseJson = await ForwardAsync(requestJson);
                await WriteMessageAsync(stdout, responseJson);
            }
        }
        catch (Exception ex)
        {
            await WriteErrorAsync(ex.Message);
            return 1;
        }
        return 0;
    }

    private static async Task<string> ForwardAsync(string requestJson)
    {
        // 确保 Tama 主进程在跑（bridge.port 存在且可连）
        var (port, token) = await EnsureBridgeAsync();

        using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        var httpReq = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/api/bridge")
        {
            Content = content,
        };
        httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await Http.SendAsync(httpReq);
        var body = await resp.Content.ReadAsStringAsync();
        return resp.IsSuccessStatusCode
            ? body
            : JsonSerializer.Serialize(new { error = $"bridge http {(int)resp.StatusCode}", detail = body });
    }

    /// <summary>读取端口文件；Tama 未启动时拉起主进程并轮询等待。</summary>
    private static async Task<(int port, string token)> EnsureBridgeAsync()
    {
        // 路径来自 AppPaths（唯一来源）。先 Initialize：本进程可能比主进程更早跑起来
        // （浏览器点了扩展、应用还没启动），旧目录的迁移要在这里也兜一次，
        // 否则它会去空目录里找 bridge.port，然后白等 15 秒超时。
        AppPaths.Initialize();
        var portFile = AppPaths.BridgePortPath;

        for (var attempt = 0; attempt < 30; attempt++)
        {
            if (File.Exists(portFile))
            {
                try
                {
                    using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(portFile));
                    var port = doc.RootElement.GetProperty("port").GetInt32();
                    var token = doc.RootElement.GetProperty("token").GetString() ?? "";
                    if (port > 0 && token.Length > 0) return (port, token);
                }
                catch { /* 文件可能正在写入，重试 */ }
            }
            else if (attempt == 0)
            {
                LaunchTama();
            }
            await Task.Delay(500);
        }
        throw new InvalidOperationException("Tama is not running (bridge.port not found)");
    }

    private static void LaunchTama()
    {
        try
        {
            // 安装脚本把 Tama.App.exe 与 NativeHost 放同一目录
            var exe = Path.Combine(AppContext.BaseDirectory, "Tama.App.exe");
            if (!File.Exists(exe)) exe = Path.Combine(AppContext.BaseDirectory, "Tama.exe");
            if (File.Exists(exe))
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            }
        }
        catch { /* 启动失败仅跳过，轮询会超时报错 */ }
    }

    // === stdin/stdout 辅助 ===

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, int count)
    {
        var total = 0;
        while (total < count)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total, count - total));
            if (n == 0) break;
            total += n;
        }
        return total;
    }

    private static async Task WriteMessageAsync(Stream stdout, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var header = BitConverter.GetBytes(bytes.Length);
        await stdout.WriteAsync(header);
        await stdout.WriteAsync(bytes);
        await stdout.FlushAsync();
    }

    private static async Task WriteErrorAsync(string message)
    {
        try
        {
            var json = JsonSerializer.Serialize(new { error = message });
            await WriteMessageAsync(Console.OpenStandardOutput(), json);
        }
        catch { /* 忽略，输出通道已坏 */ }
    }
}
