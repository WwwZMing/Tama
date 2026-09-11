using System.Text.Json;
using Tama.Core.Interfaces;
using Tama.Core.Models;
using Tama.Services.Bitwarden;

namespace Tama.Tests;

/// <summary>
/// 假的 Bitwarden 客户端：既当"同步响应"的来源，也**记录每一次上行请求**。
///
/// 为什么必须记录上行：CRUD 里最危险的 bug 是"本地一切正常，但推给服务器的东西是残的"
/// （例如收藏一张卡片时把卡片内容推成 null）。只看本地状态永远抓不到，所以用例会把
/// 记录下来的请求体**解密回一个 Cipher**，断言服务器眼里那条数据长什么样。
/// </summary>
internal sealed class FakeBitwardenClient : IBitwardenApiClient
{
    // ── 下行：同步响应 ──
    public BitwardenSyncResponse SyncResponse { get; set; } = new();

    // ── 上行记录 ──
    public List<object> CreatedBodies { get; } = new();
    public List<(string CipherId, object Body)> UpdatedBodies { get; } = new();
    public List<string> DeletedIds { get; } = new();
    public List<string> TrashedIds { get; } = new();

    // ── 文件夹上行记录 ──
    public List<string> CreatedFolderNames { get; } = new();
    public List<(string FolderId, string Name)> UpdatedFolders { get; } = new();
    public List<string> DeletedFolderIds { get; } = new();

    // ── 可配置的返回 ──
    public string? CreateReturnsId { get; set; } = Guid.NewGuid().ToString();
    public bool UpdateSucceeds { get; set; } = true;
    public bool DeleteSucceeds { get; set; } = true;
    public string? CreateFolderReturnsId { get; set; } = Guid.NewGuid().ToString();
    public bool UpdateFolderSucceeds { get; set; } = true;
    public bool DeleteFolderSucceeds { get; set; } = true;

    /// <summary>
    /// 让文件夹的三个调用**抛异常**（而不是返回 null/false）——真实的断网就是这样：
    /// HttpClient 抛 HttpRequestException，而不是给你一个"失败"返回值。这条路径以前没人挡，
    /// 结果是断网时新建文件夹直接报错、本地什么都不落。
    /// </summary>
    public bool FolderCallsThrow { get; set; }

    /// <summary>拉取次数：用来断言"闸门挡住的那次根本没发请求"。</summary>
    public int SyncCalls { get; private set; }

    /// <summary>
    /// 第 N 次（从 1 开始）SyncAsync 要抛的异常。用来演"access token 过期（401）→ 刷新 → 重试成功"，
    /// 以及"断网不该被当成过期去刷新 token"。
    /// </summary>
    public Dictionary<int, Exception> SyncFaults { get; } = new();

    /// <summary>每次 SyncAsync 实际带出去的 access token（断言重试用的是刷新后的新 token）。</summary>
    public List<string> SyncAccessTokensSeen { get; } = new();

    /// <summary>刷新 token 时**实际传出去**的那个 refresh token（必须已解密，不能带 "enc:" 前缀）。</summary>
    public string? RefreshedRefreshTokenSeen { get; private set; }
    public BitwardenRefreshResponse? RefreshReturns { get; set; } = new()
    {
        AccessToken = "new-access-token",
        RefreshToken = "new-refresh-token",
    };

    private BitwardenSyncResponse NextSync(string accessToken)
    {
        SyncCalls++;
        SyncAccessTokensSeen.Add(accessToken);
        if (SyncFaults.TryGetValue(SyncCalls, out var fault)) throw fault;
        return SyncResponse;
    }

    public Task<BitwardenSyncResponse> SyncAsync(string accessToken, string encryptionKey, DateTime? lastSync = null) =>
        Task.FromResult(NextSync(accessToken));

    public Task<BitwardenSyncResponse> SyncAsync(string accessToken, string encKey, string macKey, DateTime? lastSync = null) =>
        Task.FromResult(NextSync(accessToken));

    public Task<BitwardenCipherResponse?> CreateCipherAsync(string accessToken, string encKeyB64, string macKeyB64, object cipherRequest)
    {
        CreatedBodies.Add(cipherRequest);
        return Task.FromResult(CreateReturnsId == null
            ? null
            : new BitwardenCipherResponse { Id = CreateReturnsId, Type = 1, Name = "" });
    }

    public Task<bool> UpdateCipherAsync(string accessToken, string cipherId, object cipherRequest)
    {
        UpdatedBodies.Add((cipherId, cipherRequest));
        return Task.FromResult(UpdateSucceeds);
    }

    public Task<bool> DeleteCipherAsync(string accessToken, string cipherId)
    {
        DeletedIds.Add(cipherId);
        return Task.FromResult(DeleteSucceeds);
    }

    public Task<bool> TrashCipherAsync(string accessToken, string cipherId)
    {
        TrashedIds.Add(cipherId);
        return Task.FromResult(DeleteSucceeds);
    }

    /// <summary>
    /// 把记录下来的上行请求体当成"服务器收到并原样存下、再读回来"解一遍：
    /// 请求体是加密的匿名对象 → 序列化成 JSON → 反序列化回 raw（等同服务端 payload）
    /// → 走**真实的** DecryptCipher 与 CipherMapper.ApplyRemote。
    /// </summary>
    public static Cipher DecodeBody(object body, byte[] encKey, byte[] macKey)
    {
        var json = JsonSerializer.Serialize(body);
        var raw = JsonSerializer.Deserialize<BitwardenRawCipher>(json)!;
        var remote = BitwardenApiClient.DecryptCipher(raw, encKey, macKey, new());
        var cipher = new Cipher { Id = Guid.NewGuid() };
        CipherMapper.ApplyRemote(cipher, remote);
        return cipher;
    }

    public Task<BitwardenLoginResponse> LoginAsync(string email, string masterPassword, string? twoFactorCode = null,
        int? twoFactorProvider = null, bool newDeviceVerification = false) => throw new NotSupportedException();

    public Task<List<BitwardenCipherResponse>> GetCiphersAsync(string accessToken, string encryptionKey) => throw new NotSupportedException();

    public Task<BitwardenRefreshResponse?> RefreshTokenAsync(string refreshToken, string? accessToken = null)
    {
        RefreshedRefreshTokenSeen = refreshToken;
        return Task.FromResult(RefreshReturns);
    }

    public Task<bool> RestoreCipherAsync(string accessToken, string cipherId) => throw new NotSupportedException();

    public Task<BitwardenFolderResponse?> CreateFolderAsync(string accessToken, string name, byte[] encKey, byte[] macKey)
    {
        CreatedFolderNames.Add(name);
        if (FolderCallsThrow) throw new HttpRequestException("simulated offline");
        return Task.FromResult(CreateFolderReturnsId == null
            ? null
            : new BitwardenFolderResponse { Id = CreateFolderReturnsId, Name = name });
    }

    public Task<bool> UpdateFolderAsync(string accessToken, string folderId, string name, byte[] encKey, byte[] macKey)
    {
        UpdatedFolders.Add((folderId, name));
        if (FolderCallsThrow) throw new HttpRequestException("simulated offline");
        return Task.FromResult(UpdateFolderSucceeds);
    }

    public Task<bool> DeleteFolderAsync(string accessToken, string folderId)
    {
        DeletedFolderIds.Add(folderId);
        if (FolderCallsThrow) throw new HttpRequestException("simulated offline");
        return Task.FromResult(DeleteFolderSucceeds);
    }
}
