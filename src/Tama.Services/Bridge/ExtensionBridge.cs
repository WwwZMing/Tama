using Tama.Core.Contracts;
using Tama.Core.Exceptions;
using Serilog;

namespace Tama.Services.Bridge;

/// <summary>
/// 浏览器扩展 native-host 的 JSON 协议入口——**全解决方案唯一的字符串 JSON 边界**。
///
/// 链路：扩展（MV3）→ native messaging → Tama.NativeHost → 127.0.0.1 /api/bridge
///       （MAUI 宿主由 LocalBridgeServer 提供，Blazor Server 宿主由 BridgeController 提供）→ 本类。
///
/// 设计约束（2026-09 重构）：
///   • 方法白名单与扩展自身的 allowlist 一致（extensions/tama-webauthn/background.js:6）——
///     只有 webauthn/create|get|probe 三个方法。此前 AppService 里那张覆盖 40+ 方法的
///     反射路由表（[BridgeMethod]）是 React 时代 HTTP 全站通道的遗物，已整体删除。
///   • 业务实现零重复：本类只做 JSON ↔ 契约类型适配，业务全部转发 <see cref="IWebAuthnApi"/>。
///   • 白名单是显式 switch，无反射——方法名拼错是编译期错误，不是运行时 500。
///   • 业务失败（TamaException）与意外异常统一映射为 {"error": msg}（扩展侧只看这一个字段）；
///     意外异常的堆栈只进日志，不回传客户端。
/// </summary>
public sealed class ExtensionBridge
{
    private static readonly ILogger Log = Serilog.Log.ForContext<ExtensionBridge>();

    private readonly IWebAuthnApi _webauthn;

    public ExtensionBridge(IWebAuthnApi webauthn) => _webauthn = webauthn;

    /// <summary>处理一次 JSON 调用。永不抛异常——所有失败都折叠成 {"error": ...}。</summary>
    public async Task<string> HandleAsync(string method, string? paramsJson)
    {
        try
        {
            return await DispatchAsync(method, paramsJson);
        }
        catch (TamaException ex)
        {
            Log.Warning("Extension bridge business error {Method}: {Error}", method, ex.Message);
            return JsonRpc.Error(ex.Message);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Extension bridge error {Method}", method);
            return JsonRpc.Error(ex.Message);
        }
    }

    private Task<string> DispatchAsync(string method, string? paramsJson) => method switch
    {
        // 服务层自身对 null 请求有校验（Create 抛"rpId, challenge and origin required"、
        // Probe 返回 false），故 null params 原样透传即可，与重构前行为一致。
        "webauthn/create" => Ok(_webauthn.Create(JsonRpc.Deserialize<WebAuthnCreateRequest>(paramsJson))),
        "webauthn/get" => Ok(_webauthn.Get(JsonRpc.Deserialize<WebAuthnGetRequest>(paramsJson))),
        "webauthn/probe" => Ok(_webauthn.Probe(JsonRpc.Deserialize<WebAuthnProbeRequest>(paramsJson))),

        _ => Task.FromResult(JsonRpc.Error($"unsupported method: {method}")),
    };

    private static async Task<string> Ok<T>(Task<T> call) => JsonRpc.Ok(await call);
}
