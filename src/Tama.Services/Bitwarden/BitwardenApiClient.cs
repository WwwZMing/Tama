using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tama.Core;
using Tama.Core.Interfaces;
using Tama.Services.Crypto;
using Serilog;

namespace Tama.Services.Bitwarden;

public class BitwardenApiClient : IBitwardenApiClient
{
    private readonly HttpClient _http;
    private readonly string _identityUrl;
    private readonly string _apiUrl;
    private static readonly ILogger Log = Serilog.Log.ForContext<BitwardenApiClient>();

    public BitwardenApiClient(string serverUrl = "https://vault.bitwarden.com")
    {
        _apiUrl = serverUrl;
        _identityUrl = serverUrl.Contains("bitwarden.com")
            ? "https://identity.bitwarden.com"
            : serverUrl.Replace("vault.", "identity.").Replace("/api", "");

        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(10),
            Expect100ContinueTimeout = TimeSpan.FromSeconds(1),
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };

        // DNS 预热：后台解析避免首次请求卡在 DNS 上
        _ = Task.Run(async () =>
        {
            try
            {
                var identityHost = new Uri(_identityUrl).Host;
                var apiHost = new Uri(_apiUrl).Host;
                await Dns.GetHostAddressesAsync(identityHost);
                await Dns.GetHostAddressesAsync(apiHost);
                Log.Debug("DNS warmed up for {Identity} and {Api}", identityHost, apiHost);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "DNS warmup failed");
            }
        });
    }

    /// <summary>读不到/写不了文件时的兜底：至少同进程内稳定</summary>
    private static readonly string FallbackDeviceId = Guid.NewGuid().ToString();

    /// <summary>
    /// 本机的 Bitwarden <c>deviceIdentifier</c>：**跨进程、跨次登录稳定**（落 &lt;dataDir&gt;/device.id）。
    ///
    /// 为什么不能每次 <c>Guid.NewGuid()</c>：Bitwarden 的"新设备登录验证"是**按设备**发邮箱验证码的。
    /// 第一步（不带码）触发的码属于设备 A；第二步带着码回来时如果换了设备 B，服务端认定
    /// "这台设备没有待验证的码" → 直接失败。现象就是"跳到了验证码这一步、输了码却登录失败"。
    /// </summary>
    private static string DeviceIdentifier()
    {
        try
        {
            var path = AppPaths.DeviceIdPath;
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (existing.Length > 0) return existing;
            }
            var fresh = Guid.NewGuid().ToString();
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(path, fresh);
            Log.Information("Generated a persistent Bitwarden device id at {Path}", path);
            return fresh;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Unable to persist device id, falling back to an in-memory one");
            return FallbackDeviceId;
        }
    }

    public async Task<BitwardenLoginResponse> LoginAsync(
        string email,
        string masterPassword,
        string? twoFactorCode = null,
        int? twoFactorProvider = null,
        bool newDeviceVerification = false)
    {
        var deviceId = DeviceIdentifier();
        Log.Information("Login attempt: email={Email}, hasTwoFactorCode={HasCode}, newDeviceFlow={NewDevice}, provider={Provider}, device={Device}",
            email, twoFactorCode != null, newDeviceVerification, twoFactorProvider, deviceId);
        try
        {
            return await LoginCoreAsync(email, masterPassword, twoFactorCode, twoFactorProvider, newDeviceVerification, deviceId);
        }
        catch (Exception ex)
        {
            // 必须留痕：登录失败的具体原因以前只显示在 UI 上（日志里什么都没有），
            // "跳了验证码然后失败"到底是 400、超时、还是解密失败，只能靠猜。
            Log.Error(ex, "Bitwarden login failed: email={Email}, hasTwoFactorCode={HasCode}, newDeviceFlow={NewDevice}, device={Device}",
                email, twoFactorCode != null, newDeviceVerification, deviceId);
            throw;
        }
    }

    private async Task<BitwardenLoginResponse> LoginCoreAsync(
        string email,
        string masterPassword,
        string? twoFactorCode,
        int? twoFactorProvider,
        bool newDeviceVerification,
        string deviceId)
    {
        var prelogin = await PreloginAsync(email);
        Log.Information("Prelogin: Kdf={Kdf}, Iterations={Iterations}", prelogin.Kdf, prelogin.KdfIterations);

        // 600k 次 PBKDF2 在低端机/机器被占用时能到好几秒——单独计时，
        // 否则"点了登录之后卡住"根本分不清是在算密钥还是卡在网络
        var keyWatch = System.Diagnostics.Stopwatch.StartNew();
        var passwordKey = HashPassword(masterPassword, email, prelogin.KdfIterations);
        Log.Debug("Derived login hash in {Ms}ms (iterations={Iterations})", keyWatch.ElapsedMilliseconds, prelogin.KdfIterations);
        var passwordBase64 = Convert.ToBase64String(passwordKey);

        var emailBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(email));
        var emailBase64Url = Uri.EscapeDataString(emailBase64);

        var data = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["username"] = email,
            ["password"] = passwordBase64,
            ["scope"] = "api offline_access",
            ["client_id"] = "desktop",
            ["deviceType"] = "6",
            ["deviceIdentifier"] = deviceId,
            ["deviceName"] = "windows",
        };

        if (twoFactorCode != null)
        {
            if (newDeviceVerification)
            {
                // "新设备登录验证"的邮箱码走这个字段
                data["newDeviceOtp"] = twoFactorCode;
            }
            else
            {
                // 账号自身的两步验证走这个字段——**和上面是两个不同的字段**，
                // 以前一律发 newDeviceOtp，所以真正的 2FA 账号输了正确的码也登不上。
                data["twoFactorToken"] = twoFactorCode;
                data["twoFactorProvider"] = (twoFactorProvider ?? 0).ToString();
                data["twoFactorRemember"] = "0";
            }
        }

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_identityUrl}/connect/token")
        {
            Content = new FormUrlEncodedContent(data)
        };
        request.Headers.Add("Auth-Email", emailBase64Url);
        request.Headers.Add("device-type", "6");
        request.Headers.Add("cache-control", "no-store");
        request.Headers.Add("Bitwarden-Client-Name", "desktop");
        request.Headers.Add("Bitwarden-Client-Version", "2026.2.1");
        request.Headers.Add("Accept-Language", "zh-CN");
        request.Headers.Add("Sec-Ch-Ua", "\"Not.A/Brand\";v=\"8\", \"Chromium\";v=\"136\"");
        request.Headers.Add("Sec-Ch-Ua-Mobile", "?0");
        request.Headers.Add("Sec-Ch-Ua-Platform", "\"Windows\"");

        Log.Debug("Token request to {Url} (device={Device})", $"{_identityUrl}/connect/token", deviceId);
        var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Log.Information("Token response: {Length} chars, status {Status}", body.Length, (int)response.StatusCode);

        if (!response.IsSuccessStatusCode)
        {
            // 失败响应体如实记下来（截断）——这是"到底为什么登不上"的唯一直接证据
            Log.Warning("Token request rejected: status={Status}, body={Body}",
                (int)response.StatusCode, body.Length > 400 ? body[..400] : body);

            var error = JsonSerializer.Deserialize<BitwardenErrorResponse>(body);
            if (error?.TwoFactorProviders2 != null)
            {
                Log.Information("Server requires two-factor verification, providers={Providers}",
                    string.Join(",", error.TwoFactorProviders2.Keys));
                return new BitwardenLoginResponse { TwoFactorProviders = error.TwoFactorProviders2 };
            }
            if (body.Contains("device_error") || body.Contains("new device"))
            {
                Log.Information("Server requires new-device email verification (device={Device})", deviceId);
                return new BitwardenLoginResponse
                {
                    TwoFactorProviders = new Dictionary<int, string> { { 1, "email" } },
                    RequiresNewDeviceVerification = true,
                };
            }
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {body}");
        }

        var result = JsonSerializer.Deserialize<BitwardenTokenResponse>(body);
        if (result == null) throw new InvalidOperationException("Failed to deserialize token");

        var passwordBytes = Encoding.UTF8.GetBytes(masterPassword);
        var saltBytes = Encoding.UTF8.GetBytes(email.ToLower().Trim());
        var masterKey = KeyDerivation.Pbkdf2Sha256(masterPassword, saltBytes, prelogin.KdfIterations);

        var (stretchedEncKey, stretchedMacKey) = BitwardenCrypto.StretchMasterKey(masterKey);
        var decryptedUserKey = BitwardenCrypto.Decrypt(result.Key, stretchedEncKey, stretchedMacKey);
        if (decryptedUserKey == null || decryptedUserKey.Length != 64)
            throw new InvalidOperationException($"Failed to decrypt user key: got {decryptedUserKey?.Length ?? 0} bytes, expected 64");

        var encKey = decryptedUserKey[..32];
        var macKey = decryptedUserKey[32..];

        Log.Information("Decrypted user key: enc={EncLen}B, mac={MacLen}B", encKey.Length, macKey.Length);

        return new BitwardenLoginResponse
        {
            AccessToken = result.Access_token,
            RefreshToken = result.Refresh_token,
            Key = result.Key,
            KdfType = result.Kdf_type.ToString(),
            KdfIterations = result.Kdf_iterations,
            RawTokenKey = result.Key,
            DerivedEncKey = Convert.ToBase64String(encKey),
            DerivedMacKey = Convert.ToBase64String(macKey),
        };
    }

    private async Task<PreloginResult> PreloginAsync(string email)
    {
        var data = new { email = email.ToLower().Trim() };
        Log.Debug("Prelogin request to {Url}", $"{_identityUrl}/accounts/prelogin");
        var response = await _http.PostAsJsonAsync($"{_identityUrl}/accounts/prelogin", data);
        var body = await response.Content.ReadAsStringAsync();
        Log.Debug("Prelogin response: status={Status}, len={Len}", (int)response.StatusCode, body.Length);
        if (response.IsSuccessStatusCode)
        {
            return JsonSerializer.Deserialize<PreloginResult>(body)
                ?? new PreloginResult { Kdf = 0, KdfIterations = 600000 };
        }
        Log.Warning("Prelogin failed: {Status} {Body}", (int)response.StatusCode, body.Length > 200 ? body[..200] : body);
        return new PreloginResult { Kdf = 0, KdfIterations = 600000 };
    }

    private static byte[] HashPassword(string password, string email, int iterations)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password);
        var saltBytes = Encoding.UTF8.GetBytes(email.ToLower().Trim());
        var masterKey = KeyDerivation.Pbkdf2Sha256(password, saltBytes, iterations);
        // Bitwarden 协议第二层：以 masterKey 为密码、原密码字节为盐，固定 1 轮
        return KeyDerivation.Pbkdf2Sha256(masterKey, passwordBytes, 1);
    }

    public async Task<BitwardenSyncResponse> SyncAsync(string accessToken, string encryptionKey, DateTime? lastSync = null)
    {
        byte[] encKey, macKey;
        if (encryptionKey.Contains('.'))
        {
            var dotIndex = encryptionKey.IndexOf('.');
            var payload = encryptionKey[(dotIndex + 1)..];
            var parts = payload.Split('|');
            if (parts.Length >= 2)
            {
                var iv = Convert.FromBase64String(parts[0]);
                var ct = Convert.FromBase64String(parts[1]);
                var keyMaterial = iv.Concat(ct).ToArray();
                (encKey, macKey) = BitwardenCrypto.SplitKey(keyMaterial);
            }
            else
            {
                var keyBytes = Convert.FromBase64String(payload);
                (encKey, macKey) = BitwardenCrypto.SplitKey(keyBytes);
            }
        }
        else
        {
            var keyBytes = Convert.FromBase64String(encryptionKey);
            (encKey, macKey) = BitwardenCrypto.SplitKey(keyBytes);
        }

        return await FetchAndDecryptSyncAsync(accessToken, encKey, macKey, lastSync);
    }

    public async Task<BitwardenSyncResponse> SyncAsync(string accessToken, string encKeyB64, string macKeyB64, DateTime? lastSync = null)
    {
        var encKey = Convert.FromBase64String(encKeyB64);
        var macKey = Convert.FromBase64String(macKeyB64);
        return await FetchAndDecryptSyncAsync(accessToken, encKey, macKey, lastSync);
    }

    /// <summary>
    /// 拉取并解密同步数据。
    ///
    /// **Bitwarden 的 /api/sync 只有"全量快照"一种语义**：官方客户端同样是全量拉 + WebSocket 推增量；
    /// `lastSync` 在 Bitwarden 世界里是**客户端自己的记账字段**（`bw status` 里那个），不是服务端过滤参数。
    /// 所以这里：① 请求**不带** ?lastSync=（带了也会被服务端当没看见）；② IsFullSync 恒为 true；
    /// ③ lastSync 只作为"游标下限"参与 maxRevisionDate，防止游标回退。
    /// （旧写法拼了 ?lastSync=，服务端照样返回全量，而 IsFullSync 却按"我带了参数"算成 false
    ///   → 调用方的孤儿清理永远不会跑，"服务器上已彻底删除的条目"会永远留在本地。）
    /// </summary>
    private async Task<BitwardenSyncResponse> FetchAndDecryptSyncAsync(
        string accessToken, byte[] encKey, byte[] macKey, DateTime? lastSync)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{_apiUrl}/api/sync");
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("cache-control", "no-cache, no-store");
        request.Headers.Add("Bitwarden-Client-Name", "desktop");
        request.Headers.Add("Bitwarden-Client-Version", "2026.2.1");
        request.Headers.Add("Sec-Ch-Ua", "\"Not.A/Brand\";v=\"8\", \"Chromium\";v=\"136\"");
        request.Headers.Add("Sec-Ch-Ua-Mobile", "?0");
        request.Headers.Add("Sec-Ch-Ua-Platform", "\"Windows\"");
        var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync();

        // 诊断：Bitwarden 只有全量接口，"每次解锁都重新下载+解密整个库"的代价就在这里。
        // 顺便看服务端有没有给 ETag / Last-Modified——有的话就能做条件请求，把这一趟整个省掉。
        Log.Debug("Sync response: {Bytes} bytes, ETag={ETag}, Last-Modified={LastModified}",
            raw.Length,
            response.Headers.ETag?.ToString() ?? "-",
            response.Content.Headers.LastModified?.ToString("o") ?? "-");
        var syncData = JsonSerializer.Deserialize<BitwardenRawSyncResponse>(raw);

        // 畸形响应必须挡在这里。IsFullSync=true 的含义是"这份 payload 就是服务器完整快照"，
        // 调用方据此删掉本地那些"服务器上已经不存在"的条目——而一个被反序列化失败的、
        // 或被反向代理/自建服务器换成别的内容的 200 响应，恰好会得到
        // { profile: null, ciphers: [], folders: [] }，照单全收就是**清空用户的保险库**。
        // profile 是 sync 响应的必备字段：缺了就当畸形，宁可这次不同步。
        if (syncData?.Profile == null)
            throw new InvalidOperationException("sync 响应缺少 profile（判定为畸形响应），已放弃本次同步以避免误删本地条目");

        var orgKeys = new Dictionary<string, (byte[] encKey, byte[] macKey)>();
        if (syncData.Profile?.Organizations != null)
        {
            foreach (var org in syncData.Profile.Organizations)
            {
                if (org.Key == null) continue;
                try
                {
                    var decryptedOrgKey = BitwardenCrypto.Decrypt(org.Key, encKey, macKey);
                    if (decryptedOrgKey != null)
                    {
                        orgKeys[org.Id] = BitwardenCrypto.SplitKey(decryptedOrgKey);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to decrypt org key {OrgId}", org.Id);
                }
            }
        }

        var ciphers = new List<BitwardenCipherResponse>();
        var decryptFailures = 0;
        var maxRevisionDate = lastSync; // 以旧游标为下限，避免游标回退
        foreach (var rawCipher in syncData.Ciphers)
        {
            try
            {
                var cipher = DecryptCipher(rawCipher, encKey, macKey, orgKeys);
                ciphers.Add(cipher);
                if (maxRevisionDate == null || cipher.RevisionDate > maxRevisionDate.Value)
                    maxRevisionDate = cipher.RevisionDate;
            }
            catch (Exception ex)
            {
                // 组织 cipher 解密失败通常意味着 org key rotation（服务器已换新密钥重新加密），
                // 调用方应据此强制全量重拉一次
                if (rawCipher.OrganizationId != null) decryptFailures++;
                Log.Warning(ex, "Failed to decrypt cipher {CipherId}", rawCipher.Id);
            }
        }

        var folders = syncData.Folders.Select(f => new BitwardenFolderResponse
        {
            Id = f.Id,
            Name = BitwardenCrypto.DecryptString(f.Name, encKey, macKey) ?? "",
        }).ToList();

        return new BitwardenSyncResponse
        {
            Ciphers = ciphers,
            Folders = folders,
            Profile = syncData.Profile != null ? new BitwardenProfileResponse
            {
                Id = syncData.Profile.Id,
                Email = syncData.Profile.Email,
                Name = syncData.Profile.Name,
                Premium = syncData.Profile.Premium,
            } : null,
            IsFullSync = true,
            DecryptFailures = decryptFailures,
            MaxRevisionDate = maxRevisionDate,
        };
    }

    /// <summary>
    /// 把一条 raw cipher 解成领域响应对象。internal 是为了让单测能走**真实的**编解码路径
    /// （见 CipherMapperTests：上行请求体 JSON → 当服务端 payload 解回来）。
    /// </summary>
    internal static BitwardenCipherResponse DecryptCipher(
        BitwardenRawCipher raw,
        byte[] userEncKey, byte[] userMacKey,
        Dictionary<string, (byte[] encKey, byte[] macKey)> orgKeys)
    {
        // 组织条目用自己的 key（用组织密钥解出来的条目密钥）
        if (raw.Key != null && raw.OrganizationId != null && orgKeys.TryGetValue(raw.OrganizationId, out var orgKey))
        {
            var decryptedItemKey = BitwardenCrypto.Decrypt(raw.Key, orgKey.encKey, orgKey.macKey);
            if (decryptedItemKey != null)
            {
                var (orgEncKey, orgMacKey) = BitwardenCrypto.SplitKey(decryptedItemKey);
                var orgCipher = NewCipher(raw);
                ApplyDecryptedSections(orgCipher, raw, orgEncKey, orgMacKey);
                return orgCipher;
            }
        }

        byte[] itemEncKey, itemMacKey;
        if (raw.Key != null)
        {
            var decryptedItemKey = BitwardenCrypto.Decrypt(raw.Key, userEncKey, userMacKey);
            (itemEncKey, itemMacKey) = decryptedItemKey != null
                ? BitwardenCrypto.SplitKey(decryptedItemKey)
                : (userEncKey, userMacKey);
        }
        else
        {
            (itemEncKey, itemMacKey) = (userEncKey, userMacKey);
        }

        var result = NewCipher(raw);
        ApplyDecryptedSections(result, raw, itemEncKey, itemMacKey);
        return result;
    }

    /// <summary>响应里不需要解密的那些字段（以前在两个分支里各写了一遍）</summary>
    private static BitwardenCipherResponse NewCipher(BitwardenRawCipher raw) => new()
    {
        Id = raw.Id,
        Type = raw.Type,
        Favorite = raw.Favorite,
        FolderId = raw.FolderId,
        CreatedDate = raw.CreationDate,
        RevisionDate = raw.RevisionDate,
        DeletedDate = raw.DeletedDate,
        OrganizationId = raw.OrganizationId,
    };

    /// <summary>
    /// 四种类型 + 自定义字段 + 通行密钥的解密落点，**全在这里**。
    /// 以前只有 Login/Card/Identity——安全笔记、自定义字段、通行密钥根本没解密，
    /// 所以"拉取"即使接上也拿不到它们（DTO 里有字段但永远是 null）。
    /// </summary>
    private static void ApplyDecryptedSections(BitwardenCipherResponse c, BitwardenRawCipher raw, byte[] encKey, byte[] macKey)
    {
        c.Name = BitwardenCrypto.DecryptString(raw.Name, encKey, macKey);
        c.Notes = BitwardenCrypto.DecryptString(raw.Notes, encKey, macKey);

        if (raw.Login != null) c.Login = DecryptLogin(raw.Login, encKey, macKey);
        if (raw.Card != null) c.Card = DecryptCard(raw.Card, encKey, macKey);
        if (raw.Identity != null) c.Identity = DecryptIdentity(raw.Identity, encKey, macKey);

        // 安全笔记：只有 type，正文在 notes（上面已解）——DTO 的 Text 永远留空
        if (raw.SecureNote != null) c.SecureNote = new BitwardenSecureNoteData();

        if (raw.Fields != null)
        {
            c.Fields = raw.Fields.Select(f => new BitwardenFieldData
            {
                Name = BitwardenCrypto.DecryptString(f.Name, encKey, macKey),
                Value = BitwardenCrypto.DecryptString(f.Value, encKey, macKey),
                Type = f.Type,
                Hidden = f.Hidden,
            }).ToList();
        }

        // 通行密钥**在 login 里**（见 BitwardenRawLogin.Fido2Credentials），wire 上除 creationDate 外
        // **全是密文**。逐枚自己 try/catch：一枚解不开（MAC 对不上 / 结构畸形）不该把整条 cipher 带走——
        // DecryptCipher 的错误会被调用方记成"解密失败"，而失败的那条在孤儿清理看来就是
        // "服务器上不存在"，本地那一行会被删掉。
        if (raw.Login?.Fido2Credentials != null)
        {
            var credentials = new List<BitwardenFido2Credential>();
            foreach (var f in raw.Login.Fido2Credentials)
            {
                try
                {
                    // 非 EncString（前缀不是 "2."）原样返回：自建服务器/Vaultwarden 或历史数据里
                    // 可能是明文，宁可当明文用，也别把它当"解不开"丢掉。
                    string? D(string? v) =>
                        string.IsNullOrEmpty(v) ? null : BitwardenCrypto.DecryptString(v, encKey, macKey) ?? v;

                    var credentialId = D(f.CredentialId);
                    var keyValue = D(f.KeyValue);
                    var rpId = D(f.RpId);
                    if (string.IsNullOrEmpty(credentialId) || string.IsNullOrEmpty(keyValue) || string.IsNullOrEmpty(rpId))
                    {
                        Log.Warning("Skipping passkey without credentialId/keyValue/rpId on cipher {CipherId}", raw.Id);
                        continue;
                    }

                    var counter = D(f.Counter);
                    var discoverable = D(f.Discoverable);
                    credentials.Add(new BitwardenFido2Credential
                    {
                        CredentialId = credentialId,
                        KeyType = D(f.KeyType) ?? "public-key",
                        KeyAlgorithm = D(f.KeyAlgorithm) ?? "ECDSA",
                        KeyCurve = D(f.KeyCurve) ?? "P-256",
                        KeyValue = keyValue,
                        RpId = rpId,
                        RpName = D(f.RpName),
                        UserName = D(f.UserName),
                        UserHandle = D(f.UserHandle),
                        UserDisplayName = D(f.UserDisplayName),
                        Counter = int.TryParse(counter, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0,
                        Discoverable = string.Equals(discoverable, "true", StringComparison.OrdinalIgnoreCase),
                        CreationDate = f.CreationDate,
                    });
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to decrypt passkey on cipher {CipherId}", raw.Id);
                }
            }
            c.Fido2Credentials = credentials;
        }
    }

    private static BitwardenLoginData DecryptLogin(BitwardenRawLogin raw, byte[] encKey, byte[] macKey) => new()
    {
        Username = BitwardenCrypto.DecryptString(raw.Username, encKey, macKey),
        Password = BitwardenCrypto.DecryptString(raw.Password, encKey, macKey),
        Totp = BitwardenCrypto.DecryptString(raw.Totp, encKey, macKey),
        Uris = raw.Uris?.Select(u => new BitwardenUriData
        {
            Uri = BitwardenCrypto.DecryptString(u.Uri, encKey, macKey),
        }).ToList() ?? new(),
    };

    private static BitwardenCardData DecryptCard(BitwardenRawCard raw, byte[] encKey, byte[] macKey) => new()
    {
        CardholderName = BitwardenCrypto.DecryptString(raw.CardholderName, encKey, macKey),
        Number = BitwardenCrypto.DecryptString(raw.Number, encKey, macKey),
        Brand = BitwardenCrypto.DecryptString(raw.Brand, encKey, macKey),
        ExpMonth = BitwardenCrypto.DecryptString(raw.ExpMonth, encKey, macKey),
        ExpYear = BitwardenCrypto.DecryptString(raw.ExpYear, encKey, macKey),
        Code = BitwardenCrypto.DecryptString(raw.Code, encKey, macKey),
    };

    private static BitwardenIdentityData DecryptIdentity(BitwardenRawIdentity raw, byte[] encKey, byte[] macKey) => new()
    {
        Title = BitwardenCrypto.DecryptString(raw.Title, encKey, macKey),
        FirstName = BitwardenCrypto.DecryptString(raw.FirstName, encKey, macKey),
        MiddleName = BitwardenCrypto.DecryptString(raw.MiddleName, encKey, macKey),
        LastName = BitwardenCrypto.DecryptString(raw.LastName, encKey, macKey),
        Email = BitwardenCrypto.DecryptString(raw.Email, encKey, macKey),
        Phone = BitwardenCrypto.DecryptString(raw.Phone, encKey, macKey),
        Address1 = BitwardenCrypto.DecryptString(raw.Address1, encKey, macKey),
        Address2 = BitwardenCrypto.DecryptString(raw.Address2, encKey, macKey),
        Address3 = BitwardenCrypto.DecryptString(raw.Address3, encKey, macKey),
        City = BitwardenCrypto.DecryptString(raw.City, encKey, macKey),
        State = BitwardenCrypto.DecryptString(raw.State, encKey, macKey),
        PostalCode = BitwardenCrypto.DecryptString(raw.PostalCode, encKey, macKey),
        Country = BitwardenCrypto.DecryptString(raw.Country, encKey, macKey),
        Company = BitwardenCrypto.DecryptString(raw.Company, encKey, macKey),
        Ssn = BitwardenCrypto.DecryptString(raw.Ssn, encKey, macKey),
        Username = BitwardenCrypto.DecryptString(raw.Username, encKey, macKey),
    };

    public async Task<List<BitwardenCipherResponse>> GetCiphersAsync(string accessToken, string encryptionKey)
    {
        var sync = await SyncAsync(accessToken, encryptionKey);
        return sync.Ciphers;
    }

    private HttpRequestMessage CreateApiRequest(HttpMethod method, string path, string accessToken, object? body = null)
    {
        var url = path.StartsWith('/') ? $"{_apiUrl}{path}" : $"{_apiUrl}/{path}";
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Add("Bitwarden-Client-Name", "desktop");
        request.Headers.Add("Bitwarden-Client-Version", "2026.2.1");
        request.Headers.Add("Sec-Ch-Ua", "\"Not.A/Brand\";v=\"8\", \"Chromium\";v=\"136\"");
        request.Headers.Add("Sec-Ch-Ua-Mobile", "?0");
        request.Headers.Add("Sec-Ch-Ua-Platform", "\"Windows\"");
        if (body != null)
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
                Encoding.UTF8, "application/json");
        return request;
    }

    public async Task<BitwardenCipherResponse?> CreateCipherAsync(string accessToken, string encKeyB64, string macKeyB64, object cipherRequest)
    {
        var request = CreateApiRequest(HttpMethod.Post, "api/ciphers", accessToken, cipherRequest);
        var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        Log.Information("CreateCipher response: {Status} {Body}", (int)response.StatusCode, body.Length > 500 ? body[..500] : body);
        if (!response.IsSuccessStatusCode)
        {
            Log.Warning("CreateCipher failed: {Status} {Body}", (int)response.StatusCode, body.Length > 200 ? body[..200] : body);
            return null;
        }
        var doc = JsonDocument.Parse(body);
        var id = doc.RootElement.TryGetProperty("id", out var idProp) ? idProp.GetString() : null;
        return id != null ? new BitwardenCipherResponse { Id = id } : null;
    }

    public async Task<bool> UpdateCipherAsync(string accessToken, string cipherId, object cipherRequest)
    {
        var request = CreateApiRequest(HttpMethod.Put, $"api/ciphers/{cipherId}", accessToken, cipherRequest);
        var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            Log.Warning("UpdateCipher failed: {Status} {Body}", (int)response.StatusCode, body.Length > 200 ? body[..200] : body);
            return false;
        }
        return true;
    }

    public async Task<bool> DeleteCipherAsync(string accessToken, string cipherId)
    {
        var request = CreateApiRequest(HttpMethod.Delete, $"api/ciphers/{cipherId}", accessToken);
        var response = await _http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> TrashCipherAsync(string accessToken, string cipherId)
    {
        var request = CreateApiRequest(HttpMethod.Put, $"api/ciphers/{cipherId}/delete", accessToken);
        var response = await _http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> RestoreCipherAsync(string accessToken, string cipherId)
    {
        var request = CreateApiRequest(HttpMethod.Put, $"api/ciphers/{cipherId}/restore", accessToken);
        var response = await _http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    public async Task<BitwardenFolderResponse?> CreateFolderAsync(string accessToken, string name, byte[] encKey, byte[] macKey)
    {
        var encryptedName = BitwardenCrypto.EncryptString(name, encKey, macKey);
        var body = new { name = encryptedName };
        var request = CreateApiRequest(HttpMethod.Post, "api/folders", accessToken, body);
        var response = await _http.SendAsync(request);
        var respBody = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            Log.Warning("CreateFolder failed: {Status} {Body}", (int)response.StatusCode, respBody.Length > 200 ? respBody[..200] : respBody);
            return null;
        }
        var raw = JsonSerializer.Deserialize<BitwardenRawFolder>(respBody);
        return raw != null ? new BitwardenFolderResponse { Id = raw.Id, Name = name } : null;
    }

    public async Task<bool> UpdateFolderAsync(string accessToken, string folderId, string name, byte[] encKey, byte[] macKey)
    {
        var encryptedName = BitwardenCrypto.EncryptString(name, encKey, macKey);
        var body = new { name = encryptedName };
        var request = CreateApiRequest(HttpMethod.Put, $"api/folders/{folderId}", accessToken, body);
        var response = await _http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> DeleteFolderAsync(string accessToken, string folderId)
    {
        var request = CreateApiRequest(HttpMethod.Delete, $"api/folders/{folderId}", accessToken);
        var response = await _http.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    public async Task<BitwardenRefreshResponse?> RefreshTokenAsync(string refreshToken, string? accessToken = null)
    {
        var clientId = "desktop";
        if (!string.IsNullOrEmpty(accessToken))
        {
            try
            {
                var parts = accessToken.Split('.');
                if (parts.Length == 3)
                {
                    var payload = parts[1];
                    var padded = payload.Replace('-', '+').Replace('_', '/');
                    while (padded.Length % 4 != 0) padded += '=';
                    var json = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
                    var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("client_id", out var cid))
                        clientId = cid.GetString() ?? "desktop";
                }
            }
            catch { }
        }

        var data = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken,
        };

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_identityUrl}/connect/token")
        {
            Content = new FormUrlEncodedContent(data)
        };
        request.Headers.Add("device-type", "6");
        request.Headers.Add("cache-control", "no-store");
        request.Headers.Add("Bitwarden-Client-Name", "desktop");
        request.Headers.Add("Bitwarden-Client-Version", "2026.2.1");
        request.Headers.Add("Sec-Ch-Ua", "\"Not.A/Brand\";v=\"8\", \"Chromium\";v=\"136\"");
        request.Headers.Add("Sec-Ch-Ua-Mobile", "?0");
        request.Headers.Add("Sec-Ch-Ua-Platform", "\"Windows\"");

        var response = await _http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        Log.Information("Refresh token response: status {Status}", (int)response.StatusCode);

        if (!response.IsSuccessStatusCode)
        {
            Log.Warning("Refresh token failed: {Body}", body.Length > 200 ? body[..200] : body);
            return null;
        }

        var result = JsonSerializer.Deserialize<BitwardenTokenResponse>(body);
        if (result == null) return null;

        return new BitwardenRefreshResponse
        {
            AccessToken = result.Access_token,
            RefreshToken = result.Refresh_token,
        };
    }
}

// Raw Bitwarden API models (before decryption)
internal class BitwardenRawSyncResponse
{
    [JsonPropertyName("profile")] public BitwardenRawProfile? Profile { get; set; }
    [JsonPropertyName("ciphers")] public List<BitwardenRawCipher> Ciphers { get; set; } = new();
    [JsonPropertyName("folders")] public List<BitwardenRawFolder> Folders { get; set; } = new();
}

internal class BitwardenRawProfile
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("email")] public string Email { get; set; } = "";
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("premium")] public bool Premium { get; set; }
    [JsonPropertyName("organizations")] public List<BitwardenOrganizationKey>? Organizations { get; set; }
}

internal class BitwardenRawCipher
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public int Type { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("notes")] public string? Notes { get; set; }
    [JsonPropertyName("favorite")] public bool Favorite { get; set; }
    [JsonPropertyName("folderId")] public string? FolderId { get; set; }
    [JsonPropertyName("creationDate")] public DateTime CreationDate { get; set; }
    [JsonPropertyName("revisionDate")] public DateTime RevisionDate { get; set; }
    [JsonPropertyName("deletedDate")] public DateTime? DeletedDate { get; set; }
    [JsonPropertyName("organizationId")] public string? OrganizationId { get; set; }
    [JsonPropertyName("key")] public string? Key { get; set; }
    [JsonPropertyName("login")] public BitwardenRawLogin? Login { get; set; }
    [JsonPropertyName("card")] public BitwardenRawCard? Card { get; set; }
    [JsonPropertyName("identity")] public BitwardenRawIdentity? Identity { get; set; }
    [JsonPropertyName("secureNote")] public BitwardenRawSecureNote? SecureNote { get; set; }
    [JsonPropertyName("fields")] public List<BitwardenRawField>? Fields { get; set; }
    // 通行密钥**不在这里**，它在 login 里面（见 BitwardenRawLogin.Fido2Credentials）。
    // 曾经写在这一层 → 永远绑不到 → 同步下来的通行密钥一枚都没进过本地。
}

/// <summary>安全笔记：Bitwarden 只给 type（0=通用），正文在 cipher.notes 里。</summary>
internal class BitwardenRawSecureNote
{
    [JsonPropertyName("type")] public int Type { get; set; }
}

internal class BitwardenRawField
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("value")] public string? Value { get; set; }
    [JsonPropertyName("type")] public int Type { get; set; }
    [JsonPropertyName("hidden")] public bool Hidden { get; set; }
}

/// <summary>
/// 通行密钥（FIDO2）。**wire 上除 creationDate 外全是密文**（都是 EncString，用条目密钥加密）——
/// 依据是 bitwarden/clients 的 <c>Fido2CredentialApi</c>/<c>Fido2Credential</c>：credentialId /
/// keyType / keyAlgorithm / keyCurve / keyValue / rpId / counter / discoverable 是必需 EncString，
/// userHandle / userName / rpName / userDisplayName 是可选 EncString，只有 creationDate 是明文 ISO 时间。
/// ⚠ 这里以前把 counter 声明成 int、discoverable 声明成 bool，且只解 keyValue —— 后果是
/// **任何带通行密钥的账号一同步就整体反序列化失败**（"2.xxx|yyy" 绑不到 int），
/// 而且「启用云端通行密钥」拿到的 credentialId 是密文、压根解析不出 UUID。
/// </summary>
internal class BitwardenRawFido2Credential
{
    [JsonPropertyName("credentialId")] public string? CredentialId { get; set; }
    [JsonPropertyName("keyType")] public string? KeyType { get; set; }
    [JsonPropertyName("keyAlgorithm")] public string? KeyAlgorithm { get; set; }
    [JsonPropertyName("keyCurve")] public string? KeyCurve { get; set; }
    [JsonPropertyName("keyValue")] public string? KeyValue { get; set; }
    [JsonPropertyName("rpId")] public string? RpId { get; set; }
    [JsonPropertyName("rpName")] public string? RpName { get; set; }
    [JsonPropertyName("userName")] public string? UserName { get; set; }
    [JsonPropertyName("userHandle")] public string? UserHandle { get; set; }
    [JsonPropertyName("userDisplayName")] public string? UserDisplayName { get; set; }
    /// <summary>EncString（"true"/"false" 的密文），不是数字。</summary>
    [JsonPropertyName("counter")] public string? Counter { get; set; }
    /// <summary>EncString（"0"… 的密文），不是布尔。</summary>
    [JsonPropertyName("discoverable")] public string? Discoverable { get; set; }
    [JsonPropertyName("creationDate")] public DateTime CreationDate { get; set; }
}

internal class BitwardenRawLogin
{
    [JsonPropertyName("username")] public string? Username { get; set; }
    [JsonPropertyName("password")] public string? Password { get; set; }
    [JsonPropertyName("totp")] public string? Totp { get; set; }
    [JsonPropertyName("uris")] public List<BitwardenRawUri>? Uris { get; set; }
    /// <summary>
    /// 通行密钥**挂在 login 里面**，不是挂在 cipher 上（2026-09-13 查证并修正）。
    /// 依据：服务端 <c>CipherLoginModel.Fido2Credentials</c>、客户端 <c>cipher.request.ts</c> 的
    /// <c>this.login.fido2Credentials</c>、以及用户导出的 vault.json（同样在 login 下）。
    /// ⚠ 之前这个属性写在 <c>BitwardenRawCipher</c> 上 → 反序列化时**永远绑不到**（那一层根本没有这个键），
    /// 于是同步下来的通行密钥一枚都没进过本地：主页面「通行密钥」筛选恒为 0、
    /// 通行密钥页看不到云端那几枚、「启用云端凭据」也永远没东西可启用。
    /// 而且因为压根没绑上，<c>int Counter</c> 那个类型错误也一直没暴露（见下）。
    /// </summary>
    [JsonPropertyName("fido2Credentials")] public List<BitwardenRawFido2Credential>? Fido2Credentials { get; set; }
}

internal class BitwardenRawUri
{
    [JsonPropertyName("uri")] public string? Uri { get; set; }
}

internal class BitwardenRawCard
{
    [JsonPropertyName("cardholderName")] public string? CardholderName { get; set; }
    [JsonPropertyName("number")] public string? Number { get; set; }
    [JsonPropertyName("brand")] public string? Brand { get; set; }
    [JsonPropertyName("expMonth")] public string? ExpMonth { get; set; }
    [JsonPropertyName("expYear")] public string? ExpYear { get; set; }
    [JsonPropertyName("code")] public string? Code { get; set; }
}

internal class BitwardenRawIdentity
{
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("firstName")] public string? FirstName { get; set; }
    [JsonPropertyName("middleName")] public string? MiddleName { get; set; }
    [JsonPropertyName("lastName")] public string? LastName { get; set; }
    [JsonPropertyName("email")] public string? Email { get; set; }
    [JsonPropertyName("phone")] public string? Phone { get; set; }
    [JsonPropertyName("address1")] public string? Address1 { get; set; }
    [JsonPropertyName("address2")] public string? Address2 { get; set; }
    [JsonPropertyName("address3")] public string? Address3 { get; set; }
    [JsonPropertyName("city")] public string? City { get; set; }
    [JsonPropertyName("state")] public string? State { get; set; }
    [JsonPropertyName("postalCode")] public string? PostalCode { get; set; }
    [JsonPropertyName("country")] public string? Country { get; set; }
    [JsonPropertyName("company")] public string? Company { get; set; }
    [JsonPropertyName("ssn")] public string? Ssn { get; set; }
    [JsonPropertyName("username")] public string? Username { get; set; }
}

internal class BitwardenRawFolder
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

internal class PreloginResult
{
    [JsonPropertyName("kdf")] public int Kdf { get; set; }
    [JsonPropertyName("kdfIterations")] public int KdfIterations { get; set; } = 600000;
    [JsonPropertyName("kdfMemory")] public int? KdfMemory { get; set; }
    [JsonPropertyName("kdfParallelism")] public int? KdfParallelism { get; set; }
}

internal class BitwardenTokenResponse
{
    [JsonPropertyName("access_token")] public string Access_token { get; set; } = "";
    [JsonPropertyName("refresh_token")] public string Refresh_token { get; set; } = "";
    [JsonPropertyName("Key")] public string Key { get; set; } = "";
    [JsonPropertyName("KdfType")] public int Kdf_type { get; set; }
    [JsonPropertyName("KdfIterations")] public int Kdf_iterations { get; set; }
}

internal class BitwardenErrorResponse
{
    [JsonPropertyName("error_description")] public string? ErrorDescription { get; set; }
    [JsonPropertyName("TwoFactorProviders2")] public Dictionary<int, string>? TwoFactorProviders2 { get; set; }
    [JsonPropertyName("ErrorModel")] public BitwardenErrorModel? ErrorModel { get; set; }
}

internal class BitwardenErrorModel
{
    [JsonPropertyName("Message")] public string? Message { get; set; }
    [JsonPropertyName("Object")] public string? Object { get; set; }
}
