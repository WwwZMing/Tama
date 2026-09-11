using System.Text.Json;

namespace Tama.Services;

/// <summary>
/// 扩展 native-host JSON 协议的序列化工具（唯一使用方：<see cref="Bridge.ExtensionBridge"/>）。
/// camelCase + 大小写不敏感，与扩展侧既有的 wire 契约保持一致。
/// </summary>
public static class JsonRpc
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>成功响应。允许 null：服务层对非法请求的"成功但空结果"是合法语义（如 Probe 返回 false）。</summary>
    public static string Ok(object? data) => JsonSerializer.Serialize(data, Options);

    public static string Error(string message) => JsonSerializer.Serialize(new { error = message }, Options);

    public static T? Deserialize<T>(string? json) where T : class
        => string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<T>(json, Options);
}
