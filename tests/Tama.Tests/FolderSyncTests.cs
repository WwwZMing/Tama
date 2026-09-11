using System.Text.Json;
using Tama.Core.Contracts;
using Tama.Core.Interfaces;
using Tama.Core.Models;
using Tama.Data.Database;
using Tama.Services.Auth;
using Tama.Services.Sync;
using Tama.Services.Vault;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Tama.Tests;

/// <summary>
/// 文件夹的"离线写入队列"回路。这些用例守的是一件以前**完全没实现**的事：
/// 文件夹那几行（<c>SyncStatus/PendingOp</c>）从来没有任何消费者，
/// 于是"离线建的文件夹"永远只存在本机、界面上却一切正常。
///
/// 覆盖：三种操作（create/update/delete）都要落操作字段、都要能被队列推上去，
/// 以及推送成功后的收尾（服务器 ID 回写、指向它的条目跟着换 Id、删除要从同步缓存里清掉）。
/// </summary>
public class FolderSyncTests : IDisposable
{
    private const string AccountId = "acc-1";

    private static readonly byte[] DbKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] EncKey = Enumerable.Range(32, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] MacKey = Enumerable.Range(64, 32).Select(i => (byte)i).ToArray();

    private readonly SqliteConnection _vaultConn, _authConn;
    private readonly TamaDbContext _vault;
    private readonly AuthDbContext _auth;
    private readonly FakeBitwardenClient _client = new();
    private readonly DatabaseKeyService _keyService = new();
    private readonly BitwardenAccountService _accounts;
    private readonly FolderService _folders;
    private readonly PendingSyncProcessor _processor;

    public FolderSyncTests()
    {
        _vaultConn = new SqliteConnection("DataSource=:memory:");
        _vaultConn.Open();
        _authConn = new SqliteConnection("DataSource=:memory:");
        _authConn.Open();

        _vault = new TamaDbContext(new DbContextOptionsBuilder<TamaDbContext>().UseSqlite(_vaultConn).Options);
        _auth = new AuthDbContext(new DbContextOptionsBuilder<AuthDbContext>().UseSqlite(_authConn).Options);
        _vault.Database.EnsureCreated();
        _auth.Database.EnsureCreated();

        _keyService.SetKey(DbKey);
        _accounts = new BitwardenAccountService(_auth, _keyService, _client);
        _folders = new FolderService(_auth, _vault, _accounts, _client);
        _processor = new PendingSyncProcessor(_vault, _auth, _client, _keyService, _folders);
    }

    /// <summary>关联一个 Bitwarden 账号（真实路径就是这么存的：敏感字段先加密）</summary>
    private void LinkAccount()
    {
        var account = new AccountData
        {
            Id = AccountId,
            Email = "me@example.com",
            Type = "bitwarden",
            ServerUrl = "https://vault.bitwarden.com",
            AccessToken = "plain-access-token",
            RefreshToken = "plain-refresh-token",
            EncryptionKey = "user-key",
            DerivedEncKey = Convert.ToBase64String(EncKey),
            DerivedMacKey = Convert.ToBase64String(MacKey),
        };
        account.EncryptSensitiveFields(DbKey);
        _auth.Accounts.Add(account);
        _auth.SaveChanges();
    }

    private Folder SeedFolder(string name, string syncStatus = "synced", string? pendingOp = null)
    {
        var f = new Folder { Id = Guid.NewGuid(), Name = name, SyncStatus = syncStatus, PendingOp = pendingOp };
        _vault.Folders.Add(f);
        _vault.SaveChanges();
        return f;
    }

    private Cipher SeedCipher(string name, Guid? folderId = null)
    {
        var c = new Cipher
        {
            Id = Guid.NewGuid(),
            Type = CipherType.Login,
            Name = name,
            FolderId = folderId,
            SyncStatus = "synced",
        };
        _vault.Ciphers.Add(c);
        _vault.SaveChanges();
        return c;
    }

    /// <summary>同步缓存里也放一份（List 会把缓存并进结果，删除时最容易在这里"复活"）</summary>
    private void SeedFolderCache(Guid folderId, string name)
    {
        _auth.SyncCaches.Add(new SyncCache
        {
            AccountId = AccountId,
            FoldersJson = JsonSerializer.Serialize(new List<BitwardenFolderResponse>
            {
                new() { Id = folderId.ToString(), Name = name },
            }),
        });
        _auth.SaveChanges();
    }

    // ───────────────────────── create ─────────────────────────

    [Fact]
    public async Task Offline_Created_Folder_Is_Pushed_And_The_Ciphers_In_It_Are_Repointed()
    {
        LinkAccount();
        // 离线建的文件夹就是这个形状（FolderService.Create 推送失败时落的行）
        var local = SeedFolder("离线建的", "pending", "create");
        var inside = SeedCipher("里面的条目", local.Id);

        var serverId = Guid.NewGuid();
        _client.CreateFolderReturnsId = serverId.ToString();

        var outcome = await _processor.RunOnceAsync();

        Assert.Equal(1, outcome.Folders);
        Assert.Equal(new[] { "离线建的" }, _client.CreatedFolderNames);

        var after = await _vault.Folders.AsNoTracking().SingleAsync();
        Assert.Equal(serverId, after.Id);          // 本地 Guid → 服务器 ID（删旧行 + 插新行）
        Assert.Equal("synced", after.SyncStatus);
        Assert.Null(after.PendingOp);

        // ⚠ 指向它的条目必须跟着换 Id。不换的话它们会变成一个"指向不存在文件夹"的悬空引用，
        // 而且下一次推这些条目时会把那个本地 Guid 当 folderId 发给服务器。
        var afterCipher = await _vault.Ciphers.AsNoTracking().SingleAsync(c => c.Id == inside.Id);
        Assert.Equal(serverId, afterCipher.FolderId);
    }

    [Fact]
    public async Task Folder_Create_Failure_Keeps_It_Queued_And_Backs_Off()
    {
        LinkAccount();
        SeedFolder("推不上去", "pending", "create");
        _client.CreateFolderReturnsId = null;

        await _processor.RunOnceAsync();

        var after = await _vault.Folders.AsNoTracking().SingleAsync();
        Assert.Equal("pending", after.SyncStatus);
        Assert.Equal("create", after.PendingOp);   // 失败不能把操作字段吞掉
        Assert.Equal(1, after.RetryCount);
        Assert.NotNull(after.LastAttempt);

        // 退避：立刻再来一轮不该重试（5s × 2^1 还没到点）
        await _processor.RunOnceAsync();
        Assert.Single(_client.CreatedFolderNames);
    }

    // ───────────────────────── update ─────────────────────────

    [Fact]
    public async Task Offline_Rename_Queues_An_Update_And_The_Queue_Pushes_It()
    {
        LinkAccount();
        var folder = SeedFolder("旧名字");
        _client.UpdateFolderSucceeds = false;

        await _folders.Rename(folder.Id.ToString(), "新名字");

        var queued = await _vault.Folders.AsNoTracking().SingleAsync();
        Assert.Equal("pending", queued.SyncStatus);
        // 这一条就是"文件夹没有队列"的根：以前只改 SyncStatus，不落 PendingOp，
        // 队列元素根本认不出该 update 还是 create。
        Assert.Equal("update", queued.PendingOp);
        Assert.Equal("新名字", queued.Name);

        _client.UpdateFolderSucceeds = true;
        _client.UpdatedFolders.Clear();   // 上面那次失败的尝试也被记下来了，这里只关心队列推的那次
        var outcome = await _processor.RunOnceAsync();

        Assert.Equal(1, outcome.Folders);
        Assert.Equal((folder.Id.ToString(), "新名字"), Assert.Single(_client.UpdatedFolders));

        var after = await _vault.Folders.AsNoTracking().SingleAsync();
        Assert.Equal("synced", after.SyncStatus);
        Assert.Null(after.PendingOp);
        Assert.Equal(0, after.RetryCount);
    }

    // ───────────────────────── delete ─────────────────────────

    [Fact]
    public async Task Cloud_Folder_Delete_Queues_Until_Pushed_And_Hides_It_Everywhere()
    {
        LinkAccount();
        var folder = SeedFolder("服务器上的");
        var inside = SeedCipher("里面的条目", folder.Id);
        SeedFolderCache(folder.Id, "服务器上的");
        _client.DeleteFolderSucceeds = false;

        // 推送失败不再抛错：与条目一致，排队等后台重试（乐观删除）
        await _folders.Delete(folder.Id.ToString());

        var queued = await _vault.Folders.AsNoTracking().SingleAsync();
        Assert.Equal("pending", queued.SyncStatus);
        Assert.Equal("delete", queued.PendingOp);

        // 界面上必须立刻看不到它 —— 包括**同步缓存**里那一份（List 会把缓存并进来，最容易被它复活）
        Assert.DoesNotContain(await _folders.List(), f => f.Id == folder.Id.ToString());
        var search = await new CipherService(_vault, _accounts, _client).Search(new CipherSearchRequest());
        Assert.DoesNotContain(search.Folders, f => f.Id == folder.Id.ToString());

        _client.DeleteFolderSucceeds = true;
        _client.DeletedFolderIds.Clear();   // 上面那次失败的尝试也被记下来了，这里只关心队列推的那次
        var outcome = await _processor.RunOnceAsync();

        Assert.Equal(1, outcome.Folders);
        Assert.Equal(new[] { folder.Id.ToString() }, _client.DeletedFolderIds);
        Assert.Empty(await _vault.Folders.AsNoTracking().ToListAsync());
        // 引用它的条目回到"无文件夹"，缓存里也要清掉
        Assert.Null((await _vault.Ciphers.AsNoTracking().SingleAsync(c => c.Id == inside.Id)).FolderId);
        Assert.DoesNotContain(await _folders.List(), f => f.Name == "服务器上的");
    }

    [Fact]
    public async Task Never_Pushed_Folder_Delete_Is_Local_Only()
    {
        LinkAccount();
        var local = SeedFolder("纯本地的", "pending", "create");

        await _folders.Delete(local.Id.ToString());

        // 服务器根本不知道它存在 → 不许推 delete（推了只会拿到 404 然后永远重试一个不存在的 ID）
        Assert.Empty(_client.DeletedFolderIds);
        Assert.False(await _vault.Folders.AnyAsync());
    }

    // ───────────────────────── 新建条目时带文件夹 ─────────────────────────

    [Fact]
    public async Task Create_Cipher_With_Folder_Keeps_It_And_Pushes_The_Folder_Id()
    {
        LinkAccount();
        var folder = SeedFolder("工作");
        var svc = new CipherService(_vault, _accounts, _client);

        var r = await svc.Create(new CreateCipherRequest
        {
            Type = 1,
            Name = "带文件夹的新条目",
            FolderId = folder.Id.ToString(),
            Login = new CipherLoginRequest { Username = "u", Password = "p" },
        });

        Assert.Equal("synced", r.SyncStatus);
        // 推给服务器的那份里也要有 folderId（以前契约里没这个字段，选了等于白选）
        var onServer = FakeBitwardenClient.DecodeBody(Assert.Single(_client.CreatedBodies), EncKey, MacKey);
        Assert.Equal(folder.Id, onServer.FolderId);

        var local = await _vault.Ciphers.AsNoTracking().SingleAsync(c => c.Id == Guid.Parse(r.Id));
        Assert.Equal(folder.Id, local.FolderId);
    }

    // ───────────────────────── 断网（抛异常，不是返回失败）─────────────────────────

    [Fact]
    public async Task Offline_Create_Queues_Locally_Instead_Of_Throwing()
    {
        LinkAccount();
        _client.FolderCallsThrow = true;   // 真实断网：HttpClient 抛异常

        var r = await _folders.Create("断网时建的");

        var queued = await _vault.Folders.AsNoTracking().SingleAsync();
        Assert.Equal(r.Id, queued.Id.ToString());
        Assert.Equal("pending", queued.SyncStatus);
        Assert.Equal("create", queued.PendingOp);

        // 界面上看得见，而且带 pending 徽章（不然用户以为它已经在云端了）
        var listed = Assert.Single(await _folders.List());
        Assert.Equal("pending", listed.SyncStatus);
    }

    [Fact]
    public async Task Offline_Rename_Queues_Instead_Of_Throwing()
    {
        LinkAccount();
        var folder = SeedFolder("旧名字");
        _client.FolderCallsThrow = true;

        await _folders.Rename(folder.Id.ToString(), "新名字");

        var row = await _vault.Folders.AsNoTracking().SingleAsync();
        Assert.Equal("pending", row.SyncStatus);
        Assert.Equal("update", row.PendingOp);
        Assert.Equal("新名字", row.Name);
    }

    [Fact]
    public async Task Offline_Delete_Queues_Instead_Of_Throwing()
    {
        LinkAccount();
        var folder = SeedFolder("服务器上的");
        _client.FolderCallsThrow = true;

        await _folders.Delete(folder.Id.ToString());

        var row = await _vault.Folders.AsNoTracking().SingleAsync();
        Assert.Equal("pending", row.SyncStatus);
        Assert.Equal("delete", row.PendingOp);
        Assert.DoesNotContain(await _folders.List(), f => f.Id == folder.Id.ToString());
    }

    public void Dispose()
    {
        _vault.Dispose();
        _auth.Dispose();
        _vaultConn.Dispose();
        _authConn.Dispose();
    }
}
