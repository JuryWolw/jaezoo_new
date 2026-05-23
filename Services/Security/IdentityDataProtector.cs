using System.Security.Cryptography;
using System.Text;
using JaeZoo.Server.Data;
using JaeZoo.Server.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace JaeZoo.Server.Services.Security;

public static class IdentityDataProtector
{
    private const string Prefix = "jzid1:";
    private static readonly byte[] Aad = Encoding.UTF8.GetBytes("JaeZoo.IdentityPrivacy.v1");
    private static volatile bool _configured;
    private static byte[]? _encryptionKey;
    private static byte[]? _hashKey;

    public static bool IsConfigured => _configured;
    public static bool IsProtected(string? value) => !string.IsNullOrWhiteSpace(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

    public static void Configure(IConfiguration configuration)
    {
        var section = configuration.GetSection("IdentityPrivacy");
        var hashKeyText = section["HashKeyBase64"] ?? section["HashKey"] ?? section["HashKeyHex"];
        var encryptionKeyText = section["EncryptionKeyBase64"] ?? section["EncryptionKey"] ?? section["EncryptionKeyHex"];

        if (string.IsNullOrWhiteSpace(hashKeyText))
            throw new InvalidOperationException("Identity privacy is enabled by code, but IdentityPrivacy:HashKeyBase64 is not configured.");

        if (string.IsNullOrWhiteSpace(encryptionKeyText))
            throw new InvalidOperationException("Identity privacy is enabled by code, but IdentityPrivacy:EncryptionKeyBase64 is not configured.");

        _hashKey = NormalizeKey(hashKeyText!);
        _encryptionKey = NormalizeKey(encryptionKeyText!);
        _configured = true;
    }

    public static string HashLogin(string login) => Hmac("login", NormalizeLogin(login));
    public static string HashEmail(string email) => Hmac("email", NormalizeEmail(email));

    public static string ProtectLogin(string login) => Protect(NormalizeVisible(login));
    public static string ProtectEmail(string email) => Protect(NormalizeVisible(email));

    public static string UnprotectLogin(User user)
    {
        EnsureConfigured();
        var decrypted = TryUnprotect(user.LoginEncrypted);
        return !string.IsNullOrWhiteSpace(decrypted)
            ? decrypted
            : FallbackLegacyLogin(user);
    }

    public static string UnprotectEmail(User user)
    {
        EnsureConfigured();
        var decrypted = TryUnprotect(user.EmailEncrypted);
        return !string.IsNullOrWhiteSpace(decrypted)
            ? decrypted
            : (user.Email ?? string.Empty).Trim();
    }

    public static void SetLogin(User user, string login)
    {
        EnsureConfigured();
        login = NormalizeVisible(login);
        user.LoginHash = HashLogin(login);
        user.LoginEncrypted = ProtectLogin(login);
        SetLegacyLoginPlaceholder(user);
    }

    public static void SetEmail(User user, string email)
    {
        EnsureConfigured();
        email = NormalizeVisible(email);
        user.EmailHash = HashEmail(email);
        user.EmailEncrypted = ProtectEmail(email);
        SetLegacyEmailPlaceholder(user);
    }

    public static void SetLegacyLoginPlaceholder(User user)
    {
        var value = $"u_{user.Id:N}";
        value = value.Length <= 64 ? value : value[..64];
        user.UserName = value;
        user.Login = value;
        user.LoginNormalized = value.ToUpperInvariant();
    }

    public static void SetLegacyEmailPlaceholder(User user)
    {
        var publicPart = string.IsNullOrWhiteSpace(user.PublicId)
            ? user.Id.ToString("N")
            : user.PublicId.Trim().ToLowerInvariant().Replace("-", string.Empty, StringComparison.Ordinal);
        var value = $"{publicPart}@privacy.jaezoo.local";
        user.Email = value.Length <= 128 ? value : value[..128];
        user.EmailNormalized = user.Email.ToUpperInvariant();
    }

    public static string MaskEmailForRole(User user, bool full)
    {
        if (full)
            return UnprotectEmail(user);

        var email = UnprotectEmail(user);
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
            return string.Empty;

        var parts = email.Split('@', 2);
        var name = parts[0];
        var domain = parts[1];
        var visible = name.Length <= 2 ? name[..1] : name[..Math.Min(2, name.Length)];
        return $"{visible}***@{domain}";
    }

    public static async Task BackfillUsersAsync(AppDbContext db, ILogger logger, CancellationToken ct = default)
    {
        EnsureConfigured();

        var users = await db.Users.ToListAsync(ct);
        var changed = 0;
        foreach (var user in users)
        {
            var login = !string.IsNullOrWhiteSpace(user.LoginEncrypted)
                ? UnprotectLogin(user)
                : FallbackLegacyLogin(user);
            var email = !string.IsNullOrWhiteSpace(user.EmailEncrypted)
                ? UnprotectEmail(user)
                : (user.Email ?? string.Empty).Trim();

            if (!string.IsNullOrWhiteSpace(login) &&
                (string.IsNullOrWhiteSpace(user.LoginHash) || string.IsNullOrWhiteSpace(user.LoginEncrypted) || !IsLegacyLoginPlaceholder(user)))
            {
                SetLogin(user, login);
                changed++;
            }

            if (!string.IsNullOrWhiteSpace(email) &&
                (string.IsNullOrWhiteSpace(user.EmailHash) || string.IsNullOrWhiteSpace(user.EmailEncrypted) || !IsLegacyEmailPlaceholder(user)))
            {
                SetEmail(user, email);
                changed++;
            }

            if (user.IdentityPrivacyVersion < 1)
                user.IdentityPrivacyVersion = 1;
        }

        if (changed > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Identity privacy backfill completed. UpdatedFields={Count} Users={Users}", changed, users.Count);
        }
        else
        {
            logger.LogInformation("Identity privacy backfill checked. No user rows required changes. Users={Users}", users.Count);
        }
    }

    private static bool IsLegacyLoginPlaceholder(User user)
        => string.Equals(user.Login, $"u_{user.Id:N}", StringComparison.OrdinalIgnoreCase)
           && string.Equals(user.UserName, $"u_{user.Id:N}", StringComparison.OrdinalIgnoreCase);

    private static bool IsLegacyEmailPlaceholder(User user)
        => (user.Email ?? string.Empty).EndsWith("@privacy.jaezoo.local", StringComparison.OrdinalIgnoreCase);

    private static string FallbackLegacyLogin(User user)
        => !string.IsNullOrWhiteSpace(user.Login) ? user.Login.Trim() : (user.UserName ?? string.Empty).Trim();

    private static string NormalizeVisible(string value) => (value ?? string.Empty).Trim();
    private static string NormalizeLogin(string login) => (login ?? string.Empty).Trim().ToUpperInvariant();
    private static string NormalizeEmail(string email) => (email ?? string.Empty).Trim().ToUpperInvariant();

    private static string Hmac(string purpose, string normalized)
    {
        EnsureConfigured();
        using var hmac = new HMACSHA256(_hashKey!);
        var bytes = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{purpose}:{normalized}"));
        return Convert.ToHexString(bytes);
    }

    private static string Protect(string value)
    {
        EnsureConfigured();
        if (string.IsNullOrEmpty(value) || IsProtected(value))
            return value;

        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = Encoding.UTF8.GetBytes(value);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(_encryptionKey!, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, Aad);

        var payload = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, payload, nonce.Length + tag.Length, ciphertext.Length);
        return Prefix + Convert.ToBase64String(payload);
    }

    private static string TryUnprotect(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        if (!IsProtected(value))
            return value.Trim();

        EnsureConfigured();
        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(value[Prefix.Length..]);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("Encrypted identity value has invalid format.", ex);
        }

        if (payload.Length < 12 + 16)
            throw new CryptographicException("Encrypted identity payload is too short.");

        var nonce = payload.AsSpan(0, 12).ToArray();
        var tag = payload.AsSpan(12, 16).ToArray();
        var ciphertext = payload.AsSpan(28).ToArray();
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(_encryptionKey!, tag.Length);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, Aad);
        return Encoding.UTF8.GetString(plaintext);
    }

    private static void EnsureConfigured()
    {
        if (!_configured)
            throw new InvalidOperationException("IdentityDataProtector was not configured. Call IdentityDataProtector.Configure(...) during server startup.");
    }

    private static byte[] NormalizeKey(string keyText)
    {
        var trimmed = keyText.Trim();
        if (trimmed.Length == 64 && trimmed.All(Uri.IsHexDigit))
        {
            var result = new byte[32];
            for (var i = 0; i < result.Length; i++)
                result[i] = Convert.ToByte(trimmed.Substring(i * 2, 2), 16);
            return result;
        }

        try
        {
            var bytes = Convert.FromBase64String(trimmed);
            if (bytes.Length is 16 or 24 or 32 or 64)
                return bytes.Length == 64 ? SHA256.HashData(bytes) : bytes;
        }
        catch (FormatException)
        {
        }

        return SHA256.HashData(Encoding.UTF8.GetBytes(trimmed));
    }
}
