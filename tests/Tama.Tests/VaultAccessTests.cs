using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Tama.Core.Contracts;
using Tama.Core.Exceptions;
using Tama.Core.Interfaces;
using Tama.Data.Database;
using Tama.Services.Auth;

namespace Tama.Tests;

public sealed class VaultAccessTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "tama-access-tests", Guid.NewGuid().ToString("N"));

    public VaultAccessTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Threshold_Shares_Reconstruct_From_Any_Two()
    {
        var secret = RandomNumberGenerator.GetBytes(32);
        var shares = SecretSharing.Split(secret, threshold: 2, shareCount: 3);

        Assert.Equal(secret, SecretSharing.Combine([shares[0], shares[1]]));
        Assert.Equal(secret, SecretSharing.Combine([shares[0], shares[2]]));
        Assert.Equal(secret, SecretSharing.Combine([shares[1], shares[2]]));
    }

    [Fact]
    public void One_Share_Does_Not_Contain_The_Secret()
    {
        var secret = Enumerable.Repeat((byte)0x42, 32).ToArray();
        var shares = SecretSharing.Split(secret, threshold: 2, shareCount: 3);

        foreach (var share in shares)
            Assert.NotEqual(secret, share.Data);
    }

    [Fact]
    public void Recovery_Share_Text_Is_Validated()
    {
        var data = RandomNumberGenerator.GetBytes(32);
        var text = RecoveryShareCodec.Encode("set01", 1, 2, 3, data);
        var parsed = RecoveryShareCodec.Parse(text);

        Assert.Equal("set01", parsed.SetId);
        Assert.Equal(1, parsed.Index);
        Assert.Equal(2, parsed.Threshold);
        Assert.Equal(3, parsed.Total);
        Assert.Equal(data, parsed.Data);

        Assert.Throws<CryptographicException>(() =>
            RecoveryShareCodec.Parse(text.Replace("INDEX:1", "INDEX:2")));
    }

    [Fact]
    public void Password_Envelope_Rejects_Wrong_And_Accept_Correct()
    {
        var store = NewStore();
        var (access, key) = store.CreateForNewVault("correct horse battery staple");
        store.Save(access);

        Assert.False(store.TryUnwrapPassword("wrong", out _));
        Assert.True(store.TryUnwrapPassword("correct horse battery staple", out var unwrapped));
        Assert.Equal(key, unwrapped);
    }

    [Fact]
    public void Recovery_Passphrase_Unwraps_The_Vault_Key_And_Stores_Only_Ciphertext()
    {
        const string passphrase = "我会记得这句恢复口令 2026\n第二行也属于口令";
        var store = NewStore();
        var (access, key) = store.CreateForNewVault("master-password");
        store.Save(access);

        store.SetRecoveryPassphrase(passphrase, key);

        Assert.True(store.Status().HasRecoveryPassphrase);
        Assert.False(store.TryUnwrapRecoveryPassphrase("错误口令但长度足够长", out _));
        Assert.True(store.TryUnwrapRecoveryPassphrase(passphrase, out var unwrapped));
        Assert.Equal(key, unwrapped);

        var persisted = File.ReadAllText(store.PathOnDisk);
        Assert.DoesNotContain(passphrase, persisted, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(key), persisted, StringComparison.Ordinal);
    }

    [Fact]
    public void Recovery_Passphrase_Can_Be_Replaced_And_Cleared()
    {
        var store = NewStore();
        var (access, key) = store.CreateForNewVault("master-password");
        store.Save(access);

        store.SetRecoveryPassphrase("old recovery phrase 01", key);
        store.SetRecoveryPassphrase("new recovery phrase 02", key);

        Assert.False(store.TryUnwrapRecoveryPassphrase("old recovery phrase 01", out _));
        Assert.True(store.TryUnwrapRecoveryPassphrase("new recovery phrase 02", out var unwrapped));
        Assert.Equal(key, unwrapped);

        store.ClearRecoveryPassphrase();
        Assert.False(store.Status().HasRecoveryPassphrase);
        Assert.False(store.TryUnwrapRecoveryPassphrase("new recovery phrase 02", out _));
    }

    [Fact]
    public void Offline_Pairing_Code_Opens_Only_The_Exported_Vault_Key()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var code = OfflineTrustedDeviceCodec.CreatePairingCode();
        var vaultTag = OfflineTrustedDeviceCodec.ComputeVaultTag("vault-id");
        var envelope = OfflineTrustedDeviceCodec.SealVaultKey(
            key, code, vaultTag, "offer-id");

        Assert.False(OfflineTrustedDeviceCodec.TryOpenVaultKey(
            envelope, "AAAA-BBBB-CCCC-DDDD", vaultTag, "offer-id", out _));
        Assert.True(OfflineTrustedDeviceCodec.TryOpenVaultKey(
            envelope, code, vaultTag, "offer-id", out var opened));
        Assert.Equal(key, opened);
        Assert.Equal(
            OfflineTrustedDeviceCodec.ComputeMutualCode(key, vaultTag, "offer-id"),
            OfflineTrustedDeviceCodec.ComputeMutualCode(opened, vaultTag, "offer-id"));
    }

    [Theory]
    [InlineData("DESKTOP-123")]
    [InlineData("1号电脑")]
    [InlineData("Emily's PC")]
    public void Offline_Pairing_Device_Name_RoundTrips_As_Utf8(string deviceName)
    {
        var encoded = OfflineTrustedDeviceCodec.EncodeDeviceName(deviceName);

        Assert.NotEqual(deviceName, encoded);
        Assert.Equal(deviceName, OfflineTrustedDeviceCodec.DecodeDeviceName(encoded));
    }

    [Fact]
    public async Task Offline_Pairing_Offer_RoundTrips_Unicode_Device_Name()
    {
        var store = NewStore();
        var (access, key) = store.CreateForNewVault("master-password");
        store.Save(access);
        var keyService = new DatabaseKeyService();
        keyService.SetKey(key);
        var bio = new TestBiometricService();
        var links = new OfflineTrustedDeviceLinkStore(
            bio, Path.Combine(_root, "pairing-links", "links.json"));
        var service = new VaultAccessService(
            store, new AlwaysUnlockedAuthSession(), bio, keyService, links);

        var offer = await service.CreateTrustedDevicePairingOffer(
            new CreateTrustedDevicePairingOfferRequest { DeviceName = "1号电脑" });
        Assert.DoesNotContain("check=", offer.QrPayload, StringComparison.OrdinalIgnoreCase);
        var imported = await service.ImportTrustedDevicePairing(
            new ImportTrustedDevicePairingRequest
            {
                QrPayload = offer.QrPayload,
                PairingCode = offer.PairingCode,
            });

        Assert.Equal("1号电脑", imported.SourceDeviceName);
        Assert.Equal(offer.MutualCode, imported.MutualCode);
        var link = Assert.Single(await service.ListTrustedDeviceLinks());
        Assert.Equal("1号电脑", link.SourceDeviceName);

        var wrongMutualCode = offer.MutualCode[..5]
                              + (offer.MutualCode[5] == '0' ? '1' : '0');
        await Assert.ThrowsAsync<TamaException>(() =>
            service.CommitTrustedDevicePairing(new CommitTrustedDevicePairingRequest
            {
                OfferId = offer.OfferId,
                MutualCode = wrongMutualCode,
            }));
        Assert.Empty(store.GetOfflineTrustedDevices());

        await service.CommitTrustedDevicePairing(new CommitTrustedDevicePairingRequest
        {
            OfferId = offer.OfferId,
            MutualCode = offer.MutualCode,
        });
        Assert.Single(store.GetOfflineTrustedDevices());
        CryptographicOperations.ZeroMemory(key);
    }

    [Fact]
    public async Task Paired_Device_Can_Complete_A_Scan_Login_Without_Changing_The_Master_Password()
    {
        var sourceStore = NewStore();
        var (access, key) = sourceStore.CreateForNewVault("master-password");
        sourceStore.Save(access);

        var phoneStore = NewStore();
        phoneStore.Save(access);

        var sourceAuth = new RecordingAuthSession();
        var sourceKeyService = new DatabaseKeyService();
        sourceKeyService.SetKey(key);
        var sourceBio = new TestBiometricService();
        var sourceLinks = new OfflineTrustedDeviceLinkStore(
            sourceBio, Path.Combine(_root, "source-links", "links.json"));
        var source = new VaultAccessService(
            sourceStore, sourceAuth, sourceBio, sourceKeyService, sourceLinks);

        var phoneAuth = new RecordingAuthSession();
        var phoneKeyService = new DatabaseKeyService();
        var phoneBio = new TestBiometricService();
        var phoneLinks = new OfflineTrustedDeviceLinkStore(
            phoneBio, Path.Combine(_root, "phone-links", "links.json"));
        var phone = new VaultAccessService(
            phoneStore, phoneAuth, phoneBio, phoneKeyService, phoneLinks);

        var pairing = await source.CreateTrustedDevicePairingOffer(
            new CreateTrustedDevicePairingOfferRequest { DeviceName = "Windows PC" });
        var imported = await phone.ImportTrustedDevicePairing(
            new ImportTrustedDevicePairingRequest
            {
                QrPayload = pairing.QrPayload,
                PairingCode = pairing.PairingCode,
            });
        await source.CommitTrustedDevicePairing(new CommitTrustedDevicePairingRequest
        {
            OfferId = pairing.OfferId,
            MutualCode = imported.MutualCode,
        });

        var loginOffer = await source.CreateTrustedDeviceLoginOffer(
            new CreateTrustedDeviceLoginOfferRequest { DeviceName = "Windows PC" });
        Assert.StartsWith("tama://login?", loginOffer.QrPayload, StringComparison.Ordinal);
        Assert.DoesNotContain(
            OfflineTrustedDeviceCodec.NormalizePairingCode(pairing.PairingCode),
            loginOffer.QrPayload,
            StringComparison.Ordinal);
        Assert.DoesNotContain(pairing.PairingCode, loginOffer.QrPayload, StringComparison.Ordinal);

        var prompt = await phone.ReadTrustedDeviceLoginRequest(loginOffer.QrPayload);
        Assert.Equal(pairing.OfferId, prompt.SourceDeviceId);
        Assert.Equal("Windows PC", prompt.SourceDeviceName);

        var recoveryCode = await phone.RevealTrustedDeviceRecoveryCode(prompt.LinkId);
        await source.UnlockWithTrustedDeviceCode(
            new UnlockWithTrustedDeviceCodeRequest { LoginCode = recoveryCode.Code });

        Assert.Null(sourceAuth.LastNewPassword);
        Assert.Equal(key, sourceAuth.LastVaultKey);
        Assert.True(sourceStore.TryUnwrapPassword("master-password", out var unchangedKey));
        Assert.Equal(key, unchangedKey);
        CryptographicOperations.ZeroMemory(unchangedKey);

        var sourceDevices = await source.ListPairedTrustedDevices();
        var sourceDevice = Assert.Single(sourceDevices);
        Assert.Equal(PairedTrustedDeviceSide.AuthorizedDevice, sourceDevice.Side);
        var phoneDevices = await phone.ListPairedTrustedDevices();
        var phoneDevice = Assert.Single(phoneDevices);
        Assert.Equal(PairedTrustedDeviceSide.SavedRecoveryLink, phoneDevice.Side);
        Assert.Equal("Windows PC", phoneDevice.Name);

        await phone.RemovePairedTrustedDevice(
            new PairedTrustedDeviceId(phoneDevice.Id, phoneDevice.Side));
        Assert.Empty(await phone.ListPairedTrustedDevices());
        Assert.Single(await source.ListPairedTrustedDevices());

        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(sourceAuth.LastVaultKey);
    }

    [Fact]
    public void Offline_Trusted_Device_Can_Recover_And_Is_Consumed_By_Caller()
    {
        var store = NewStore();
        var (access, key) = store.CreateForNewVault("master-password");
        store.Save(access);
        var code = OfflineTrustedDeviceCodec.CreatePairingCode();
        var vaultTag = OfflineTrustedDeviceCodec.ComputeVaultTag(access.VaultId);
        var envelope = OfflineTrustedDeviceCodec.SealVaultKey(
            key, code, vaultTag, "offer-1");

        store.AddOfflineTrustedDevice(
            "offline-offer-1", "手机", "offer-1", vaultTag, envelope);

        Assert.Single(store.GetOfflineTrustedDevices());
        Assert.True(store.TryUnwrapOfflineTrustedDevice(
            code, out var matched, out var recovered));
        Assert.Equal(key, recovered);
        Assert.Equal("offline-offer-1", matched?.Id);

        store.RemoveTrustedDevice("offline-offer-1");
        Assert.Empty(store.GetOfflineTrustedDevices());
        Assert.False(store.TryUnwrapOfflineTrustedDevice(code, out _, out _));
    }

    [Fact]
    public async Task Offline_Link_Store_Returns_Record_Id_And_Saves_Code_As_Ciphertext()
    {
        var path = Path.Combine(_root, "links", "trusted-device-links.json");
        var store = new OfflineTrustedDeviceLinkStore(new TestBiometricService(), path);
        var code = OfflineTrustedDeviceCodec.CreatePairingCode();

        var linkId = await store.Save("offer-7", "备用手机", "vault-tag", "294816", code);

        Assert.NotEqual(OfflineTrustedDeviceCodec.NormalizePairingCode(code), linkId);
        var link = Assert.Single(store.List());
        Assert.Equal(linkId, link.Id);
        Assert.Equal("备用手机", link.SourceDeviceName);

        var revealed = await store.Reveal(linkId);
        Assert.Equal(
            OfflineTrustedDeviceCodec.FormatCode(
                OfflineTrustedDeviceCodec.NormalizePairingCode(code)),
            revealed.Code);
        Assert.DoesNotContain(code, await File.ReadAllTextAsync(path), StringComparison.Ordinal);
    }

    [Fact]
    public void Recovery_Kit_Unwraps_With_Any_Two_Shares_And_Nothing_Changes_Before_Commit()
    {
        var store = NewStore();
        var (access, key) = store.CreateForNewVault("master-password");
        store.Save(access);

        var kit = store.CreateRecoveryKit(key);
        Assert.False(store.Status().HasRecovery);

        store.CommitRecoveryKit(kit.EnvelopeJson);
        Assert.True(store.Status().HasRecovery);
        Assert.Equal(key, store.UnwrapRecovery(kit.Shares[0], kit.Shares[2]));
        Assert.Equal(key, store.UnwrapRecovery(kit.Shares[1], kit.Shares[2]));
    }

    [Fact]
    public void Recovery_Kit_Must_Use_Two_Different_Valid_Shares()
    {
        var store = NewStore();
        var (access, key) = store.CreateForNewVault("master-password");
        store.Save(access);
        var kit = store.CreateRecoveryKit(key);
        store.CommitRecoveryKit(kit.EnvelopeJson);

        Assert.Throws<CryptographicException>(() =>
            store.UnwrapRecovery(kit.Shares[0]));
        Assert.Throws<CryptographicException>(() =>
            store.UnwrapRecovery(kit.Shares[0], kit.Shares[0]));
    }

    [Fact]
    public void Replacing_Recovery_Kit_Makes_Old_Device_Share_Stale()
    {
        var store = NewStore();
        var (access, key) = store.CreateForNewVault("master-password");
        store.Save(access);

        var oldKit = store.CreateRecoveryKit(key);
        store.CommitRecoveryKit(oldKit.EnvelopeJson);
        var oldShare = RecoveryShareCodec.Parse(oldKit.Shares[0]);
        Assert.True(store.IsCurrentRecoveryShare(oldShare));

        var newKit = store.CreateRecoveryKit(key);
        store.CommitRecoveryKit(newKit.EnvelopeJson);

        Assert.False(store.IsCurrentRecoveryShare(oldShare));
        Assert.True(store.IsCurrentRecoveryShare(RecoveryShareCodec.Parse(newKit.Shares[0])));
        Assert.ThrowsAny<CryptographicException>(() =>
            store.UnwrapRecovery(oldKit.Shares[0], oldKit.Shares[1]));
    }

    [Fact]
    public void Password_Change_Only_Rewraps_The_Same_Vault_Key()
    {
        var store = NewStore();
        var (access, key) = store.CreateForNewVault("old-password");
        store.Save(access);
        store.UpdatePassword(access, "new-password", key);

        Assert.False(store.TryUnwrapPassword("old-password", out _));
        Assert.True(store.TryUnwrapPassword("new-password", out var unwrapped));
        Assert.Equal(key, unwrapped);
    }

    [Fact]
    public void Recovery_Key_Opens_The_Same_Encrypted_Database_After_Password_Rewrap()
    {
        var store = NewStore();
        var (access, key) = store.CreateForNewVault("forgotten-password");
        store.Save(access);

        var kit = store.CreateRecoveryKit(key);
        store.CommitRecoveryKit(kit.EnvelopeJson);

        var dbPath = Path.Combine(_root, "vault.db");
        var dbPassword = Convert.ToHexString(key);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
               {
                   DataSource = dbPath,
                   Mode = SqliteOpenMode.ReadWriteCreate,
                   Password = dbPassword,
               }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE secrets (value TEXT NOT NULL);";
            command.ExecuteNonQuery();
        }

        var recoveredKey = store.UnwrapRecovery(kit.Shares[0], kit.Shares[2]);
        Assert.Equal(key, recoveredKey);
        Assert.True(DatabaseEncryptionHelper.TryOpen(dbPath, Convert.ToHexString(recoveredKey)));

        store.UpdatePassword(access, "replacement-password", recoveredKey);
        Assert.False(store.TryUnwrapPassword("forgotten-password", out _));
        Assert.True(store.TryUnwrapPassword("replacement-password", out var replacementKey));
        Assert.Equal(key, replacementKey);
        Assert.True(DatabaseEncryptionHelper.TryOpen(dbPath, Convert.ToHexString(replacementKey)));
    }

    private VaultAccessStore NewStore()
    {
        var dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return new VaultAccessStore(new VaultCryptoService(),
            Path.Combine(dir, "vault-access.json"),
            Path.Combine(dir, "device-trust.id"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}

internal sealed class TestBiometricService : IBiometricService
{
    private bool _configured;

    public bool IsAvailable() => true;
    public bool IsConfigured() => _configured;
    public void SetupKey() { }

    public string EncryptPassword(string password)
        => "test:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(password));

    public string? DecryptPassword(string encrypted)
    {
        if (!encrypted.StartsWith("test:", StringComparison.Ordinal)) return null;
        return System.Text.Encoding.UTF8.GetString(
            Convert.FromBase64String(encrypted["test:".Length..]));
    }

    public void SaveEncryptedPassword(string encrypted) => _configured = true;
    public string? GetEncryptedPassword() => null;
    public void ClearEncryptedPassword() => _configured = false;
    public Task<bool> AuthenticateAsync(string title, string subtitle) => Task.FromResult(true);
}

internal sealed class AlwaysUnlockedAuthSession : IAuthSession
{
    public bool IsSetup => true;
    public bool IsUnlocked => true;
    public string? Password => null;
    public bool HasModernDeviceCredential => true;
    public bool RequireMasterPasswordRecheck() => false;

    public Task<bool> IsSetupAsync() => Task.FromResult(true);
    public Task SetupAsync(string password) => Task.CompletedTask;
    public Task<bool> UnlockAsync(string password) => Task.FromResult(true);
    public Task ApplyVaultKeyAsync(byte[] vaultKey, string? newPassword = null) => Task.CompletedTask;
    public void Lock() { }
}

internal sealed class RecordingAuthSession : IAuthSession
{
    public bool IsSetup => true;
    public bool IsUnlocked { get; private set; } = true;
    public string? Password => null;
    public bool HasModernDeviceCredential => true;
    public byte[] LastVaultKey { get; private set; } = [];
    public string? LastNewPassword { get; private set; }
    public bool RequireMasterPasswordRecheck() => false;

    public Task<bool> IsSetupAsync() => Task.FromResult(true);
    public Task SetupAsync(string password) => Task.CompletedTask;
    public Task<bool> UnlockAsync(string password) => Task.FromResult(true);

    public Task ApplyVaultKeyAsync(byte[] vaultKey, string? newPassword = null)
    {
        LastVaultKey = vaultKey.ToArray();
        LastNewPassword = newPassword;
        IsUnlocked = true;
        return Task.CompletedTask;
    }

    public void Lock() => IsUnlocked = false;
}
