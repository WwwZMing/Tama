namespace Tama.Core.Models;

public class CipherLogin
{
    public string? Username { get; set; }
    public string? Password { get; set; }
    public List<string> Uris { get; set; } = new();
    public string? Totp { get; set; }
}
