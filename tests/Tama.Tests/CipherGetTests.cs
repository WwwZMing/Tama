using Tama.Data.Database;
using Tama.Services.Vault;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Tama.Tests;

/// <summary>
/// 单条查询（IVaultApi.Get）：列表行展开 / 详情页 / 编辑页都靠它按需取那一条，
/// 取代了旧的"Search 全库再按 Id 挑"。这里盯住四件事：
/// 取得到、敏感字段带得出来、取不到时返回 null 而不是抛、非法 Id 不炸。
/// </summary>
public class CipherGetTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly TamaDbContext _db, _db2;
    private readonly CipherService _svc;
    private readonly Guid _id = Guid.NewGuid();
    private readonly Guid _deletedId = Guid.NewGuid();

    public CipherGetTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        var options = new DbContextOptionsBuilder<TamaDbContext>().UseSqlite(_conn).Options;
        _db = new TamaDbContext(options);
        _db.Database.EnsureCreated();

        _db.Ciphers.Add(new Tama.Core.Models.Cipher
        {
            Id = _id,
            Type = Tama.Core.Models.CipherType.Login,
            Name = "GitHub",
            Notes = "备注",
            Login = new Tama.Core.Models.CipherLogin
            {
                Username = "octocat",
                Password = "s3cret",
                Totp = "JBSWY3DPEHPK3PXP",
                Uris = new() { "https://github.com" },
            },
        });
        _db.Ciphers.Add(new Tama.Core.Models.Cipher
        {
            Id = _deletedId,
            Type = Tama.Core.Models.CipherType.Login,
            Name = "AlreadyDeleted",
            DeletedAt = DateTime.UtcNow,
            Login = new Tama.Core.Models.CipherLogin { Uris = new() },
        });
        _db.SaveChanges();

        // 服务是 Scoped，测试里手动给第二个上下文，确保读的是库里的数据而不是被跟踪的实体
        _db2 = new TamaDbContext(options);
        _svc = new CipherService(_db2, null!, null!);
    }

    [Fact]
    public async Task Get_Returns_Cipher_With_Sensitive_Fields()
    {
        var dto = await _svc.Get(_id.ToString());

        Assert.NotNull(dto);
        Assert.Equal("GitHub", dto!.Name);
        Assert.Equal("备注", dto.Notes);
        Assert.Equal("octocat", dto.Login?.Username);
        Assert.Equal("s3cret", dto.Login?.Password);
        Assert.Equal("JBSWY3DPEHPK3PXP", dto.Login?.Totp);
        Assert.Equal(new List<string> { "https://github.com" }, dto.Login?.Uris);
    }

    [Fact]
    public async Task Get_Returns_Null_For_Missing_Cipher()
        => Assert.Null(await _svc.Get(Guid.NewGuid().ToString()));

    [Fact]
    public async Task Get_Returns_Null_For_Soft_Deleted_Cipher()
        => Assert.Null(await _svc.Get(_deletedId.ToString()));

    /// <summary>路由参数直接来自 URL：非法 Id 必须是"取不到"，不能把异常甩到页面上</summary>
    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-00000000000")]
    public async Task Get_Returns_Null_For_Invalid_Id(string id)
        => Assert.Null(await _svc.Get(id));

    public void Dispose()
    {
        _db.Dispose();
        _db2.Dispose();
        _conn.Dispose();
    }
}
