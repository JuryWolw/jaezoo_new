using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace JaeZoo.Server.Services.Security;

public static class MessageTextProtector
{
    private const string Prefix = "jzenc1:";
    private static readonly byte[] Aad = Encoding.UTF8.GetBytes("JaeZoo.MessageText.v1");
    private static volatile bool _configured;
    private static volatile bool _enabled;
    private static byte[]? _key;

    public static bool Enabled => _enabled;
    public static bool IsProtected(string? value) => !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);

    public static void Configure(IConfiguration configuration)
    {
        var section = configuration.GetSection("Messages:Encryption");
        var configuredEnabled = section.GetValue<bool?>("Enabled");
        var keyText = section["KeyBase64"] ?? section["Key"] ?? section["KeyHex"];
        var hasKey = !string.IsNullOrWhiteSpace(keyText);

        _enabled = configuredEnabled ?? hasKey;
        if (!_enabled)
        {
            _key = null;
            _configured = true;
            return;
        }

        if (!hasKey)
            throw new InvalidOperationException("Messages encryption is enabled, but Messages:Encryption:KeyBase64 is not configured.");

        _key = NormalizeKey(keyText!);
        _configured = true;
    }

    public static string ProtectForDatabase(string? value)
    {
        EnsureConfigured();
        var plain = value ?? string.Empty;
        if (!_enabled || string.IsNullOrEmpty(plain) || IsProtected(plain))
            return plain;

        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintext = Encoding.UTF8.GetBytes(plain);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];

        using var aes = new AesGcm(_key!, tag.Length);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, Aad);

        var payload = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, payload, nonce.Length + tag.Length, ciphertext.Length);

        return Prefix + Convert.ToBase64String(payload);
    }

    public static string UnprotectFromDatabase(string? value)
    {
        EnsureConfigured();
        var stored = value ?? string.Empty;
        if (string.IsNullOrEmpty(stored) || !IsProtected(stored))
            return stored;

        if (!_enabled)
            throw new InvalidOperationException("Encrypted message text was found, but message encryption is disabled on this server.");

        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(stored[Prefix.Length..]);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("Encrypted message text has invalid format.", ex);
        }

        if (payload.Length < 12 + 16)
            throw new CryptographicException("Encrypted message text payload is too short.");

        var nonce = payload.AsSpan(0, 12).ToArray();
        var tag = payload.AsSpan(12, 16).ToArray();
        var ciphertext = payload.AsSpan(28).ToArray();
        var plaintext = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(_key!, tag.Length);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, Aad);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException ex)
        {
            throw new CryptographicException("Failed to decrypt message text. Check Messages:Encryption key.", ex);
        }
    }

    private static void EnsureConfigured()
    {
        if (!_configured)
            throw new InvalidOperationException("MessageTextProtector was not configured. Call MessageTextProtector.Configure(...) during server startup.");
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
            if (bytes.Length is 16 or 24 or 32)
                return bytes;
        }
        catch (FormatException)
        {
            // Fall through to SHA-256 derivation below.
        }

        // Allows readable env secrets while still producing a valid AES-256 key.
        // Prefer a generated 32-byte base64 key in production.
        return SHA256.HashData(Encoding.UTF8.GetBytes(trimmed));
    }
}
