using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Isopoh.Cryptography.Argon2;
using Tama.Core.Contracts;

namespace Tama.Services.Import;

/// <summary>
/// Standalone KDBX 3.1 / 4.x parser — no SkiaSharp, no KeePassLibStd.
/// Supports AES-256-CBC + AES-KDF (KDBX 3.1) and ChaCha20 + Argon2id (KDBX 4.x).
/// </summary>
public static class KdbxParser
{
    public sealed class KdbxEntry
    {
        public string? Title { get; set; }
        public string? UserName { get; set; }
        public string? Password { get; set; }
        public string? URL { get; set; }
        public string? Notes { get; set; }
        public string? TOTP { get; set; }
        public DateTime CreationTime { get; set; }
        public DateTime LastModificationTime { get; set; }
        public Dictionary<string, string> CustomFields { get; set; } = new();
        public List<KdbxEntry> SubEntries { get; set; } = new(); // nested groups

        /// <summary>所属分组路径（顶层组名开头，嵌套用 / 连接，如 "个人/银行"）；根级条目为 null。</summary>
        public string? GroupPath { get; set; }
    }

    // KDBX signatures (LE)
    private const uint KdbxSig1 = 0x9AA2D903;
    private const uint KdbxSig2 = 0xB54BFB67;

    // Header field type IDs
    private const byte FldEnd = 0;
    private const byte FldComment = 1;
    private const byte FldCipherId = 2;
    private const byte FldCompression = 3;
    private const byte FldMasterSeed = 4;
    private const byte FldTransformSeed = 5; // not in v4
    private const byte FldTransformRounds = 6; // not in v4
    private const byte FldEncryptionIV = 7;
    private const byte FldProtectedStreamKey = 8;
    private const byte FldStreamStartBytes = 9;
    private const byte FldInnerRandomStreamId = 10;
    private const byte FldKdfParameters = 11; // v4 only
    private const byte FldPublicCustomData = 12; // v4 only

    // Cipher UUIDs
    private static readonly Guid CipherAes256 = new(0x31C1F2E6, 0xBF71, 0x4350, 0xBE, 0x58, 0x05, 0x21, 0x6A, 0xFC, 0x5A, 0xFF);
    private static readonly Guid CipherChaCha20 = new(0xD6038A2B, 0x8B6F, 0x4CB5, 0xA5, 0x24, 0x33, 0x9A, 0x31, 0xDB, 0xB5, 0x9A);
    // KDF UUIDs
    private static readonly Guid KdfAes = new(0xC9D9F39A, 0x628A, 0x4460, 0xBF, 0x74, 0x0D, 0x08, 0xC1, 0x8A, 0x4F, 0xEB);
    private static readonly Guid KdfArgon2 = new(0xC9D9F39A, 0x628A, 0x4460, 0xBF, 0x74, 0x0D, 0x08, 0xC1, 0x8A, 0x4F, 0xEA);

    // ---------- public API ----------

    /// <returns>A flat list of entries (groups flattened, group info lost).</returns>
    public static List<KdbxEntry> Parse(byte[] data, string? password)
    {
        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms);

        // --- signature & version ---
        if (br.ReadUInt32() != KdbxSig1 || br.ReadUInt32() != KdbxSig2)
            throw new FormatException("Not a valid KDBX file.");

        uint ver = br.ReadUInt32();
        bool isV4 = ver >= 0x00040000;

        // --- header ---
        var (cipherId, compression, masterSeed, transformSeed, transformRounds,
             encryptionIV, streamStartBytes, kdfParams) = ReadHeader(br, isV4);

        // --- key derivation ---
        var passwordBytes = Encoding.UTF8.GetBytes(password ?? "");
        var compositeKey = HashSha256(HashSha256(passwordBytes)); // no keyfile

        byte[] transformedKey;
        if (isV4 && kdfParams != null)
        {
            transformedKey = DeriveKeyV4(compositeKey, kdfParams);
        }
        else
        {
            transformedKey = AesKdf(compositeKey, transformSeed!, (int)transformRounds);
        }

        var finalKey = HashSha256(Concat(masterSeed, transformedKey));

        // --- decrypt ---
        byte[] encryptedPayload;
        if (isV4)
        {
        // v4: first 32 bytes = HMAC-SHA256, then ChaCha20-encrypted rest
        br.ReadBytes(32); // skip HMAC (not verified for import)
        encryptedPayload = br.ReadBytes((int)(ms.Length - ms.Position));
        }
        else
        {
            encryptedPayload = br.ReadBytes((int)(ms.Length - ms.Position));
        }

        byte[] decrypted;
        Guid cipherGuid = new(cipherId);
        if (cipherGuid == CipherChaCha20)
        {
            decrypted = ChaCha20Decrypt(finalKey.Take(32).ToArray(), encryptionIV.Take(12).ToArray(), encryptedPayload);
        }
        else
        {
            decrypted = AesCbcDecrypt(finalKey.Take(32).ToArray(), encryptionIV, encryptedPayload);
        }

        // --- verify stream start bytes & skip them ---
        if (decrypted.Length < 32)
            throw new FormatException("Decrypted data too short.");
        // streamStartBytes 是文件头里的随机标记：解密后前 32 字节必须与之一致，
        // 不一致 = 密码错误或文件损坏（KDBX 规范的官方校验点）
        if (streamStartBytes is { Length: 32 } expected &&
            !decrypted.AsSpan(0, 32).SequenceEqual(expected))
        {
            throw new FormatException("Wrong password or corrupted file (stream start bytes mismatch).");
        }
        var inner = new byte[decrypted.Length - 32];
        Buffer.BlockCopy(decrypted, 32, inner, 0, inner.Length);

        // --- decompress ---
        byte[] xmlBytes = compression == 1 ? GZipDecompress(inner) : inner;

        // --- parse XML ---
        return ParseXml(xmlBytes);
    }

    /// <summary>
    /// 解析 KDBX 并返回顶层分组摘要（名称 + 条目数，按出现顺序）。轻量——只列分组不返回条目，
    /// 供导入流程让用户选择"导入哪个文件夹"（全量导入时条目可能成百上千，预览会塞爆）。
    /// </summary>
    public static List<KdbxGroupDto> ParseGroups(byte[] data, string? password)
    {
        var names = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in Parse(data, password))
        {
            if (entry.GroupPath == null) continue;
            var top = entry.GroupPath.Split('/')[0];
            if (counts.TryGetValue(top, out var c))
                counts[top] = c + 1;
            else
            {
                counts[top] = 1;
                names.Add(top);
            }
        }
        return names.Select(n => new KdbxGroupDto(n, counts[n])).ToList();
    }

    // ---------- header ----------

    private static (byte[] cipherId, int compression, byte[] masterSeed, byte[]? transformSeed,
        ulong transformRounds, byte[] encryptionIV, byte[] streamStartBytes, Dictionary<string, object>? kdfParams)
        ReadHeader(BinaryReader br, bool isV4)
    {
        byte[]? cipherId = null, masterSeed = null, transformSeed = null,
                encryptionIV = null, streamStartBytes = null;
        int compression = 0;
        ulong transformRounds = 0;
        Dictionary<string, object>? kdfParams = null;

        while (true)
        {
            byte fieldType = br.ReadByte();
            if (fieldType == FldEnd) break;

            int fieldLen = isV4 ? ReadVarInt(br) : br.ReadUInt16();
            switch (fieldType)
            {
                case FldComment:
                case FldPublicCustomData:
                    br.ReadBytes(fieldLen); // skip
                    break;
                case FldCipherId:
                    cipherId = br.ReadBytes(fieldLen);
                    break;
                case FldCompression:
                    compression = (int)br.ReadUInt32();
                    break;
                case FldMasterSeed:
                    masterSeed = br.ReadBytes(fieldLen);
                    break;
                case FldTransformSeed:
                    transformSeed = br.ReadBytes(fieldLen);
                    break;
                case FldTransformRounds:
                    transformRounds = br.ReadUInt64();
                    break;
                case FldEncryptionIV:
                    encryptionIV = br.ReadBytes(fieldLen);
                    break;
                case FldProtectedStreamKey:
                    br.ReadBytes(fieldLen); // skip — only needed for protected fields
                    break;
                case FldStreamStartBytes:
                    streamStartBytes = br.ReadBytes(fieldLen);
                    break;
                case FldInnerRandomStreamId:
                    br.ReadBytes(fieldLen);
                    break;
                case FldKdfParameters:
                    kdfParams = ReadKdfParams(br, fieldLen);
                    break;
                default:
                    br.ReadBytes(fieldLen);
                    break;
            }
        }

        if (cipherId == null || masterSeed == null || encryptionIV == null || streamStartBytes == null)
            throw new FormatException("Missing required header fields.");

        return (cipherId, compression, masterSeed, transformSeed, transformRounds,
                encryptionIV, streamStartBytes, kdfParams);
    }

    // ---------- KDF ----------

    private static byte[] AesKdf(byte[] key, byte[] seed, int rounds)
    {
        using var aes = Aes.Create();
        aes.Key = seed;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        aes.IV = new byte[16];

        var result = (byte[])key.Clone();
        using var encryptor = aes.CreateEncryptor();
        for (int i = 0; i < rounds; i++)
        {
            result = encryptor.TransformFinalBlock(result, 0, 16);
        }
        return HashSha256(Concat(seed, result));
    }

    private static byte[] DeriveKeyV4(byte[] compositeKey, Dictionary<string, object> kdfParams)
    {
        if (!kdfParams.TryGetValue("$UUID", out var uuidObj))
            throw new FormatException("KDF UUID missing.");
        byte[] uuidBytes = ((string)uuidObj!).Split(',')
            .Select(byte.Parse).ToArray();

        var kdfUuid = new Guid(uuidBytes);

        if (kdfUuid == KdfAes)
        {
            // v4 can also use AES-KDF
            var seed = GetKdfBytes(kdfParams, "S");
            var rounds = GetKdfUInt64(kdfParams, "R");
            return AesKdf(compositeKey, seed, (int)rounds);
        }
        else if (kdfUuid == KdfArgon2)
        {
            var salt = GetKdfBytes(kdfParams, "S");
            var parallelism = (int)GetKdfUInt32(kdfParams, "P");
            var memory = (int)(GetKdfUInt64(kdfParams, "M") / 1024); // KiB
            var iterations = (int)GetKdfUInt64(kdfParams, "I");

            var config = new Argon2Config
            {
                Type = Argon2Type.HybridAddressing, // Argon2id
                Version = Argon2Version.Nineteen,
                Password = compositeKey,
                Salt = salt,
                TimeCost = iterations,
                MemoryCost = memory,
                Lanes = parallelism,
                Threads = parallelism,
                HashLength = 32,
            };
            var argon2 = new Argon2(config);
            using var hashHandle = argon2.Hash();
            var result = new byte[32];
            Buffer.BlockCopy(hashHandle.Buffer, 0, result, 0, 32);
            return result;
        }

        throw new NotSupportedException($"Unsupported KDF: {kdfUuid}");
    }

    // ---------- encryption ----------

    private static byte[] AesCbcDecrypt(byte[] key, byte[] iv, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(data, 0, data.Length);
    }

    private static byte[] ChaCha20Decrypt(byte[] key, byte[] nonce, byte[] data)
    {
        // ChaCha20 is self-inverse: encrypt == decrypt
        var result = new byte[data.Length];
        int blocks = (data.Length + 63) / 64;
        for (int block = 0; block < blocks; block++)
        {
            var keystream = ChaCha20Block(key, nonce, (uint)block);
            int offset = block * 64;
            int chunkLen = Math.Min(64, data.Length - offset);
            for (int i = 0; i < chunkLen; i++)
                result[offset + i] = (byte)(data[offset + i] ^ keystream[i]);
        }
        return result;
    }

    private static byte[] ChaCha20Block(byte[] key, byte[] nonce, uint counter)
    {
        // ChaCha20 state: "expand 32-byte k" + key + counter + nonce
        var state = new uint[16];
        state[0] = 0x61707865; // "expa"
        state[1] = 0x3320646E; // "nd 3"
        state[2] = 0x79622D32; // "2-by"
        state[3] = 0x6B206574; // "te k"
        for (int i = 0; i < 8; i++)
            state[4 + i] = BitConverter.ToUInt32(key, i * 4);
        state[12] = counter;
        for (int i = 0; i < 3; i++)
            state[13 + i] = BitConverter.ToUInt32(nonce, i * 4);

        var working = (uint[])state.Clone();
        for (int i = 0; i < 10; i++)
        {
            QuarterRound(ref working, 0, 4, 8, 12);
            QuarterRound(ref working, 1, 5, 9, 13);
            QuarterRound(ref working, 2, 6, 10, 14);
            QuarterRound(ref working, 3, 7, 11, 15);
            QuarterRound(ref working, 0, 5, 10, 15);
            QuarterRound(ref working, 1, 6, 11, 12);
            QuarterRound(ref working, 2, 7, 8, 13);
            QuarterRound(ref working, 3, 4, 9, 14);
        }

        var output = new byte[64];
        for (int i = 0; i < 16; i++)
        {
            var val = working[i] + state[i];
            BitConverter.GetBytes(val).CopyTo(output, i * 4);
        }
        return output;
    }

    private static void QuarterRound(ref uint[] s, int a, int b, int c, int d)
    {
        s[a] += s[b]; s[d] ^= s[a]; s[d] = RotL(s[d], 16);
        s[c] += s[d]; s[b] ^= s[c]; s[b] = RotL(s[b], 12);
        s[a] += s[b]; s[d] ^= s[a]; s[d] = RotL(s[d], 8);
        s[c] += s[d]; s[b] ^= s[c]; s[b] = RotL(s[b], 7);
    }

    private static uint RotL(uint x, int n) => (x << n) | (x >> (32 - n));

    // ---------- XML ----------

    private static List<KdbxEntry> ParseXml(byte[] xmlBytes)
    {
        using var ms = new MemoryStream(xmlBytes);
        var doc = XDocument.Load(ms);
        var root = doc.Root;
        if (root == null) return new();

        var entries = new List<KdbxEntry>();
        var rootGroup = root.Element("Root")?.Element("Group"); // KeePass 的 Root 虚拟组
        if (rootGroup == null) return entries;

        // Root 虚拟组下直接挂的条目（极少数）：无分组路径
        foreach (var entryEl in rootGroup.Elements("Entry"))
            entries.Add(ParseEntry(entryEl));

        // 每个顶层真实分组独立作为路径起点（"个人"），嵌套追加 "/子组"（"个人/银行"）
        foreach (var topGroup in rootGroup.Elements("Group"))
            WalkGroup(topGroup, entries, null);
        return entries;
    }

    private static void WalkGroup(XElement? groupEl, List<KdbxEntry> entries, string? path)
    {
        if (groupEl == null) return;

        // 当前组名：顶层（path==null）直接作路径；嵌套组名拼到现有路径后
        var groupName = groupEl.Element("Name")?.Value;
        string? childPath = path;
        if (!string.IsNullOrEmpty(groupName))
            childPath = path == null ? groupName : $"{path}/{groupName}";

        foreach (var entryEl in groupEl.Elements("Entry"))
        {
            var entry = ParseEntry(entryEl);
            entry.GroupPath = childPath;
            entries.Add(entry);
        }

        foreach (var childGroup in groupEl.Elements("Group"))
            WalkGroup(childGroup, entries, childPath);
    }

    private static KdbxEntry ParseEntry(XElement el)
    {
        var entry = new KdbxEntry();

        foreach (var stringEl in el.Element("String")?.Elements("String") ?? Enumerable.Empty<XElement>())
        {
            var key = stringEl.Element("Key")?.Value ?? "";
            var value = stringEl.Element("Value")?.Value ?? "";

            switch (key)
            {
                case "Title": entry.Title = value; break;
                case "UserName": entry.UserName = value; break;
                case "Password": entry.Password = value; break;
                case "URL": entry.URL = value; break;
                case "Notes": entry.Notes = value; break;
                case "otp": entry.TOTP = value; break;
                default:
                    if (!key.StartsWith("Protected ") && !key.StartsWith("S:"))
                        entry.CustomFields[key] = value;
                    break;
            }
        }

        // timestamps
        if (TryParseDateTime(el.Element("Times")?.Element("CreationTime")?.Value, out var ct))
            entry.CreationTime = ct;
        if (TryParseDateTime(el.Element("Times")?.Element("LastModificationTime")?.Value, out var lmt))
            entry.LastModificationTime = lmt;

        return entry;
    }

    // ---------- KDF params helper ----------

    // v4 KDF params dict format:
    //   version: uint16 LE
    //   entries until type==0:
    //     type:    1 byte  (1=byte[], 2=uint32, 3=uint64, 4=bool, 5=string)
    //     key len: varint, then key name (UTF8)
    //     val len: varint, then value
    private static Dictionary<string, object> ReadKdfParams(BinaryReader br, int len)
    {
        long end = br.BaseStream.Position + len;
        br.ReadUInt16(); // version (2 bytes LE)
        var dict = new Dictionary<string, object>();

        while (br.BaseStream.Position < end)
        {
            byte type = br.ReadByte();
            if (type == 0) break;

            int keyLen = ReadVarInt(br);
            string key = Encoding.UTF8.GetString(br.ReadBytes(keyLen));
            int valLen = ReadVarInt(br);

            object value = type switch
            {
                1 => string.Join(",", br.ReadBytes(valLen)), // byte[] -> comma-separated
                2 => (object)br.ReadUInt32(),
                3 => (object)br.ReadUInt64(),
                4 => (object)(br.ReadByte() != 0),
                5 => Encoding.UTF8.GetString(br.ReadBytes(valLen)),
                _ => br.ReadBytes(valLen),
            };
            dict[key] = value;
        }
        return dict;
    }

    private static byte[] GetKdfBytes(Dictionary<string, object> dict, string key)
    {
        if (!dict.TryGetValue(key, out var v))
            throw new FormatException($"KDF param '{key}' missing.");
        return ((string)v!).Split(',').Select(byte.Parse).ToArray();
    }

    private static uint GetKdfUInt32(string key, object val) =>
        val is uint u ? u : Convert.ToUInt32(val);

    private static uint GetKdfUInt32(Dictionary<string, object> dict, string key) =>
        GetKdfUInt32(key, dict[key]);

    private static ulong GetKdfUInt64(Dictionary<string, object> dict, string key) =>
        dict[key] is ulong u ? u : Convert.ToUInt64(dict[key]);

    // ---------- varint ----------

    private static int ReadVarInt(BinaryReader br)
    {
        int result = 0, shift = 0;
        while (true)
        {
            byte b = br.ReadByte();
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        return result;
    }

    // ---------- utility ----------

    private static byte[] HashSha256(byte[] data)
    {
        using var sha = SHA256.Create();
        return sha.ComputeHash(data);
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, r, 0, a.Length);
        Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
        return r;
    }

    private static byte[] GZipDecompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }

    private static bool TryParseDateTime(string? s, out DateTime dt)
    {
        dt = default;
        if (string.IsNullOrWhiteSpace(s)) return false;
        // KeePass XML uses ISO 8601, sometimes with 'T', sometimes with ' '
        return DateTime.TryParse(s.Replace(' ', 'T'), null,
            System.Globalization.DateTimeStyles.RoundtripKind, out dt);
    }
}
