using System.Security.Cryptography;
using RadioDeck.Models;

namespace RadioDeck.Services;

/// <summary>
/// The optional settings PIN is stored as a salted PBKDF2 hash, never in clear text.
/// It keeps operators from changing radio addresses by accident; it is not a defence
/// against someone who can edit config.json.
/// </summary>
public static class PinHasher
{
    private const int Iterations = 100_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public static void SetPin(SecurityConfig security, string? pin)
    {
        if (string.IsNullOrEmpty(pin))
        {
            security.PinSalt = "";
            security.PinHash = "";
            return;
        }

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        security.PinSalt = Convert.ToBase64String(salt);
        security.PinHash = Convert.ToBase64String(Hash(pin, salt));
    }

    public static bool Verify(SecurityConfig security, string? pin)
    {
        if (!security.HasPin) return true;
        if (string.IsNullOrEmpty(pin)) return false;
        try
        {
            var salt = Convert.FromBase64String(security.PinSalt);
            var expected = Convert.FromBase64String(security.PinHash);
            return CryptographicOperations.FixedTimeEquals(Hash(pin, salt), expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] Hash(string pin, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(pin, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
}
