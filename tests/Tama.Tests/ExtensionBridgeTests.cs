using System.Text.Json;
using Tama.Core.Contracts;
using Tama.Core.Exceptions;
using Tama.Services.Bridge;

namespace Tama.Tests;

/// <summary>
/// 扩展 native-host 边界契约测试（ExtensionBridge）。
///
/// 这条 JSON 通道是浏览器扩展唯一能触达 Tama 的路径，所以这里锁定的是**扩展真实读取的字段名**：
///   extensions/tama-webauthn/content-main.js:83-96 读 response.clientDataJSON / attestationObject /
///   authenticatorData / signature / userHandle 与顶层 id / rawId / publicKey / publicKeyAlgorithm / transports；
///   content-main.js:130 读 probe 的 hasMatch；deserializeCredential 认 {"error"} 信封。
/// 字段名一旦被序列化策略改坏（如 clientDataJSON 变成 clientDataJson），扩展当场失效且编译期无感——
/// 这正是本测试存在的理由。
/// </summary>
public class ExtensionBridgeTests
{
    // === 1. 响应形状：与扩展读取的字段名逐一对应 ===

    [Fact]
    public async Task Create_Serializes_Fields_Extension_Actually_Reads()
    {
        var api = new FakeWebAuthn
        {
            OnCreate = _ => Task.FromResult(new WebAuthnCreateResponse(
                Success: null, Reason: null,
                Id: "cred1", RawId: "cred1", Type: "public-key",
                PublicKey: "c3BraQ", PublicKeyAlgorithm: -257,
                Transports: new List<string> { "internal" },
                Response: new WebAuthnAttestationData("Y2xpZW50", "YXR0ZXN0", "YXV0aGRhdGE"),
                AuthenticatorAttachment: "platform", RpId: "example.com", UserName: "u@example.com")),
        };
        var bridge = new ExtensionBridge(api);

        var json = await bridge.HandleAsync("webauthn/create", """{"rpId":"example.com"}""");
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // 顶层（content-main.js deserializeCredential）
        Assert.Equal("cred1", root.GetProperty("id").GetString());
        Assert.Equal("cred1", root.GetProperty("rawId").GetString());
        Assert.Equal("c3BraQ", root.GetProperty("publicKey").GetString());
        Assert.Equal(-257, root.GetProperty("publicKeyAlgorithm").GetInt32());
        Assert.Equal("internal", root.GetProperty("transports")[0].GetString());
        Assert.Equal("example.com", root.GetProperty("rpId").GetString());

        // response 内层：全大写缩写必须原样保留（CamelCase 策略只小写首字母）
        var response = root.GetProperty("response");
        Assert.Equal("Y2xpZW50", response.GetProperty("clientDataJSON").GetString());
        Assert.Equal("YXR0ZXN0", response.GetProperty("attestationObject").GetString());
        Assert.Equal("YXV0aGRhdGE", response.GetProperty("authenticatorData").GetString());

        // 成功响应：success 为 null（JsonRpc.Options 不忽略 null，与重构前逐字节一致），
        // 关键是不能变成 false——扩展把 success:false 当业务失败处理。
        Assert.Equal(JsonValueKind.Null, root.GetProperty("success").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("reason").ValueKind);
    }

    [Fact]
    public async Task Get_Serializes_Assertion_Fields()
    {
        var api = new FakeWebAuthn
        {
            OnGet = _ => Task.FromResult(new WebAuthnGetResponse(
                Success: null, Reason: null,
                Id: "cred1", RawId: "cred1", Type: "public-key",
                Response: new WebAuthnAssertionData("Y2xpZW50", "YXV0aGRhdGE", "c2ln", "dXNlcg"),
                RpId: "example.com", Counter: 7)),
        };
        var bridge = new ExtensionBridge(api);

        var json = await bridge.HandleAsync("webauthn/get", """{"rpId":"example.com","challenge":"Y2g"}""");
        using var doc = JsonDocument.Parse(json);
        var response = doc.RootElement.GetProperty("response");

        Assert.Equal("c2ln", response.GetProperty("signature").GetString());
        Assert.Equal("dXNlcg", response.GetProperty("userHandle").GetString());
        Assert.Equal("YXV0aGRhdGE", response.GetProperty("authenticatorData").GetString());
        Assert.Equal(7, doc.RootElement.GetProperty("counter").GetInt32());
    }

    [Fact]
    public async Task Probe_Serializes_HasMatch()
    {
        var bridge = new ExtensionBridge(new FakeWebAuthn { OnProbe = _ => Task.FromResult(new WebAuthnProbeResponse(true)) });

        var json = await bridge.HandleAsync("webauthn/probe", """{"rpId":"example.com"}""");

        // content-main.js:130 判据：probe.hasMatch !== true 就放行原生认证器
        Assert.Equal("""{"hasMatch":true}""", json);
    }

    // === 2. 请求反序列化：扩展发来的 camelCase 载荷必须还原 ===

    [Fact]
    public async Task Request_Deserializes_Extension_Payload()
    {
        WebAuthnGetRequest? seen = null;
        var api = new FakeWebAuthn
        {
            OnGet = req =>
            {
                seen = req;
                return Task.FromResult(new WebAuthnGetResponse(null, null, null, null, null, null, null, 0));
            },
        };
        var bridge = new ExtensionBridge(api);

        // content-main.js:143-149 真实载荷形状（allowCredentials 传字符串数组）
        await bridge.HandleAsync("webauthn/get", """
            {"rpId":"example.com","challenge":"Y2hhbGxlbmdl","origin":"https://example.com",
             "credentialId":"Y3JlZA","allowCredentialIds":["Y3JlZA","b3RoZXI"]}
            """);

        Assert.NotNull(seen);
        Assert.Equal("example.com", seen!.RpId);
        Assert.Equal("Y2hhbGxlbmdl", seen.Challenge);
        Assert.Equal("https://example.com", seen.Origin);
        Assert.Equal(2, seen.AllowCredentialIds!.Count);
        Assert.Equal("b3RoZXI", seen.AllowCredentialIds[1]);
    }

    [Fact]
    public async Task Missing_Params_Reach_Service_As_Null()
    {
        // 扩展允许不带 params；服务层对 null 有校验（Probe 返回 false、Create 抛业务错误）
        var probeSawNull = false;
        var api = new FakeWebAuthn
        {
            OnProbe = req =>
            {
                probeSawNull = req is null;
                return Task.FromResult(new WebAuthnProbeResponse(false));
            },
        };
        var bridge = new ExtensionBridge(api);

        await bridge.HandleAsync("webauthn/probe", null);

        Assert.True(probeSawNull);
    }

    // === 3. 方法白名单：React 时代那张 40+ 方法的反射路由表已删除 ===

    [Theory]
    [InlineData("auth/status")]
    [InlineData("cipher/search")]
    [InlineData("auth/unlock")]
    [InlineData("vault/export")]
    [InlineData("biometric/load")]
    [InlineData("theme/changed")]
    public async Task Non_Whitelisted_Method_Is_Rejected_Without_Touching_Api(string method)
    {
        // 白名单外的方法必须在进入服务层之前被拒（FakeWebAuthn 未挂钩子时调用即抛异常）
        var bridge = new ExtensionBridge(new FakeWebAuthn());

        var json = await bridge.HandleAsync(method, "{}");

        Assert.Equal($$"""{"error":"unsupported method: {{method}}"}""", json);
    }

    // === 4. 错误信封 ===

    [Fact]
    public async Task TamaException_Maps_To_Error_Envelope()
    {
        var api = new FakeWebAuthn
        {
            OnCreate = _ => throw new TamaException("rpId, challenge and origin required"),
        };
        var bridge = new ExtensionBridge(api);

        var json = await bridge.HandleAsync("webauthn/create", "{}");

        Assert.Equal("""{"error":"rpId, challenge and origin required"}""", json);
    }

    [Fact]
    public async Task Unexpected_Exception_Maps_To_Error_Envelope_Without_Leaking_Stack()
    {
        var api = new FakeWebAuthn
        {
            OnGet = _ => throw new InvalidOperationException("boom"),
        };
        var bridge = new ExtensionBridge(api);

        var json = await bridge.HandleAsync("webauthn/get", "{}");

        using var doc = JsonDocument.Parse(json);
        Assert.Equal("boom", doc.RootElement.GetProperty("error").GetString());
        // 重构前意外异常会把 stackTrace/innerError 回传客户端——现在只进日志
        Assert.False(doc.RootElement.TryGetProperty("stackTrace", out _));
        Assert.False(doc.RootElement.TryGetProperty("innerError", out _));
    }

    [Fact]
    public async Task Business_Failure_Shape_Stays_On_Success_Path()
    {
        // 业务失败（用户取消）不是 {"error"}：扩展读 success/reason 而不是抛异常
        var api = new FakeWebAuthn
        {
            OnCreate = _ => Task.FromResult(new WebAuthnCreateResponse(
                Success: false, Reason: "user-cancelled",
                Id: null, RawId: null, Type: null, PublicKey: null, PublicKeyAlgorithm: null,
                Transports: null, Response: null, AuthenticatorAttachment: null, RpId: null, UserName: null)),
        };
        var bridge = new ExtensionBridge(api);

        var json = await bridge.HandleAsync("webauthn/create", """{"rpId":"example.com"}""");
        using var doc = JsonDocument.Parse(json);

        Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("user-cancelled", doc.RootElement.GetProperty("reason").GetString());
        Assert.False(doc.RootElement.TryGetProperty("error", out _));
    }

    /// <summary>
    /// 扩展通道只用到 WebAuthn 三个方法，所以替身只需实现这三个。
    /// 未挂钩子就被调用 = 白名单外的方法穿透到了服务层，直接抛异常暴露。
    /// </summary>
    private sealed class FakeWebAuthn : IWebAuthnApi
    {
        public Func<WebAuthnCreateRequest?, Task<WebAuthnCreateResponse>>? OnCreate { get; init; }
        public Func<WebAuthnGetRequest?, Task<WebAuthnGetResponse>>? OnGet { get; init; }
        public Func<WebAuthnProbeRequest?, Task<WebAuthnProbeResponse>>? OnProbe { get; init; }

        public Task<WebAuthnCreateResponse> Create(WebAuthnCreateRequest? req) =>
            OnCreate?.Invoke(req) ?? throw new InvalidOperationException("Create 不应被调用（白名单外）");

        public Task<WebAuthnGetResponse> Get(WebAuthnGetRequest? req) =>
            OnGet?.Invoke(req) ?? throw new InvalidOperationException("Get 不应被调用（白名单外）");

        public Task<WebAuthnProbeResponse> Probe(WebAuthnProbeRequest? req) =>
            OnProbe?.Invoke(req) ?? throw new InvalidOperationException("Probe 不应被调用（白名单外）");
    }
}
