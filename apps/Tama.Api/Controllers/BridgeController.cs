using Tama.Services.Bridge;
using Microsoft.AspNetCore.Mvc;

namespace Tama.Api.Controllers;

/// <summary>
/// 浏览器扩展 native-host 的 HTTP 入口（/api/bridge）。
/// 与 MAUI 宿主的 LocalBridgeServer 共用同一实现 <see cref="ExtensionBridge"/>，
/// 业务再往下走同一套领域服务——只有一份实现。
/// 注意：本宿主未做鉴权，仅限本机回环使用。
/// </summary>
[ApiController]
[Route("api")]
public class BridgeController : ControllerBase
{
    private readonly ExtensionBridge _bridge;

    public BridgeController(ExtensionBridge bridge)
    {
        _bridge = bridge;
    }

    [HttpPost("bridge")]
    public async Task<IActionResult> Bridge([FromBody] BridgeRequest request)
    {
        if (string.IsNullOrEmpty(request?.Method))
            return BadRequest(new { error = "method required" });

        var result = await _bridge.HandleAsync(request.Method, request.Params);
        return Content(result, "application/json");
    }
}

public class BridgeRequest
{
    public string? Method { get; set; }

    /// <summary>JSON 序列化后的参数（与 native host 的 params 字段一致）</summary>
    public string? Params { get; set; }
}
