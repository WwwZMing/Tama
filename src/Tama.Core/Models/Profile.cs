namespace Tama.Core.Models;

public class Profile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = string.Empty;
    public string? Name { get; set; }
    public bool Premium { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
