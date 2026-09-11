using Tama.Core.Contracts;
using Tama.Data.Database;
using Tama.Services.Vault;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Tama.Tests;

/// <summary>CipherSearch 基本行为：本地表有条目时必须全部返回（回归：真机曾出现列表空但导出有数据）。</summary>
public class CipherSearchTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly TamaDbContext _db;

    public CipherSearchTests()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        _conn.Open();
        var options = new DbContextOptionsBuilder<TamaDbContext>().UseSqlite(_conn).Options;
        _db = new TamaDbContext(options);
        _db.Database.EnsureCreated();
    }

    [Fact]
    public async Task CipherSearch_Returns_All_NonDeleted()
    {
        for (int i = 0; i < 3; i++)
        {
            _db.Ciphers.Add(new Tama.Core.Models.Cipher
            {
                Id = Guid.NewGuid(),
                Type = Tama.Core.Models.CipherType.Login,
                Name = $"Site{i}",
                Login = new Tama.Core.Models.CipherLogin { Username = $"u{i}", Password = $"p{i}", Uris = new() },
            });
        }
        // 一条已软删除的
        _db.Ciphers.Add(new Tama.Core.Models.Cipher
        {
            Id = Guid.NewGuid(),
            Type = Tama.Core.Models.CipherType.Login,
            Name = "Deleted",
            DeletedAt = DateTime.UtcNow,
            Login = new Tama.Core.Models.CipherLogin { Uris = new() },
        });
        await _db.SaveChangesAsync();

        var svc = new CipherService(_db, null!, null!);
        var r = await svc.Search(new CipherSearchRequest());

        Assert.Equal(3, r.Ciphers.Count);
        Assert.DoesNotContain(r.Ciphers, c => c.Name == "Deleted");
    }

    public void Dispose()
    {
        _db.Dispose();
        _conn.Dispose();
    }
}
