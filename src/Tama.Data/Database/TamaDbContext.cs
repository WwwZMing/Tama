using Microsoft.EntityFrameworkCore;
using Tama.Core.Models;

namespace Tama.Data.Database;

public class TamaDbContext : DbContext
{
    public DbSet<Cipher> Ciphers => Set<Cipher>();
    public DbSet<Folder> Folders => Set<Folder>();
    public DbSet<Profile> Profiles => Set<Profile>();
    public DbSet<PasskeyCredentialRecord> PasskeyCredentials => Set<PasskeyCredentialRecord>();

    public TamaDbContext(DbContextOptions<TamaDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Cipher>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Type).HasConversion<string>();
            entity.OwnsOne(e => e.Login);
            entity.OwnsOne(e => e.Card);
            entity.OwnsOne(e => e.Identity);
            entity.OwnsOne(e => e.SecureNote);
            entity.OwnsMany(e => e.Fido2Credentials, fb =>
            {
                // ⚠ 必须是 ValueGeneratedNever：自有集合默认把键属性当 store-generated，
                //   于是 EF 把这个 int 列从 INSERT 里省掉，而 SQLite 在复合主键 (CipherId, Id) 下
                //   没有生成它的机制 → NOT NULL constraint failed: Fido2Credential.Id（实测）。
                //   表结构不能改（宿主走 EnsureCreated，老库不会迁移），所以改成由写入方按
                //   条目内 0,1,2… 编号（见 Fido2Credential.Id 的注释）。
                fb.Property(f => f.Id).ValueGeneratedNever();
                fb.Property(f => f.CredentialId).HasMaxLength(500);
                fb.Property(f => f.RpId).HasMaxLength(255);
            });
            entity.OwnsMany(e => e.Fields, fb =>
            {
                // 同 Fido2Credentials：复合主键 (CipherId, Id) 里的 Id 必须由写入方编号，
                // 不能指望 SQLite 生成（实测：NOT NULL constraint failed: CipherField.Id）
                fb.Property(f => f.Id).ValueGeneratedNever();
                fb.Property(f => f.Name).HasMaxLength(255);
            });
            entity.Property(e => e.Tags).HasConversion(
                v => string.Join(",", v),
                v => v.Split(",", StringSplitOptions.RemoveEmptyEntries).ToList());
        });

        modelBuilder.Entity<Folder>(entity =>
        {
            entity.HasKey(e => e.Id);
        });

        modelBuilder.Entity<Profile>(entity =>
        {
            entity.HasKey(e => e.Id);
        });

        modelBuilder.Entity<PasskeyCredentialRecord>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.CredentialId).IsUnique();
        });

        // VaultFingerprint 已移到 fingerprint.json 文件，不再存储在数据库
    }
}
