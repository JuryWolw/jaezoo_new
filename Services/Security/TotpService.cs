using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace JaeZoo.Server.Services.Security;

public static class TotpService
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(30);

    public static string GenerateSecret(int bytes = 20)
        => ToBase32(RandomNumberGenerator.GetBytes(bytes));

    public static string NormalizeCode(string? code)
        => new string((code ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    public static bool VerifyTotp(string base32Secret, string? code, DateTime? utcNow = null, int allowedDriftSteps = 1)
    {
        var normalized = NormalizeCode(code);
        if (normalized.Length != 6 || !normalized.All(char.IsDigit))
            return false;

        var now = utcNow ?? DateTime.UtcNow;
        var counter = GetCounter(now);
        for (var offset = -allowedDriftSteps; offset <= allowedDriftSteps; offset++)
        {
            var expected = ComputeTotp(base32Secret, counter + offset);
            if (FixedTimeEquals(expected, normalized))
                return true;
        }

        return false;
    }

    public static string ComputeTotp(string base32Secret, long counter)
    {
        var key = FromBase32(base32Secret);
        Span<byte> counterBytes = stackalloc byte[8];
        var value = counter;
        for (var i = 7; i >= 0; i--)
        {
            counterBytes[i] = (byte)(value & 0xff);
            value >>= 8;
        }

        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(counterBytes.ToArray());
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24)
                     | ((hash[offset + 1] & 0xff) << 16)
                     | ((hash[offset + 2] & 0xff) << 8)
                     | (hash[offset + 3] & 0xff);
        var otp = binary % 1_000_000;
        return otp.ToString("D6", CultureInfo.InvariantCulture);
    }

    public static long GetCounter(DateTime utcNow)
    {
        var unix = new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc)).ToUnixTimeSeconds();
        return unix / (long)Step.TotalSeconds;
    }

    public static string BuildOtpAuthUri(string issuer, string accountLabel, string secret)
    {
        issuer = Uri.EscapeDataString(string.IsNullOrWhiteSpace(issuer) ? "JaeZoo" : issuer.Trim());
        accountLabel = Uri.EscapeDataString(string.IsNullOrWhiteSpace(accountLabel) ? "JaeZoo" : accountLabel.Trim());
        return $"otpauth://totp/{issuer}:{accountLabel}?secret={secret}&issuer={issuer}&algorithm=SHA1&digits=6&period=30";
    }

    public static IReadOnlyList<string> GenerateRecoveryCodes(int count = 10)
    {
        var result = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var bytes = RandomNumberGenerator.GetBytes(10);
            var raw = ToBase32(bytes).Replace("=", string.Empty, StringComparison.Ordinal);
            raw = raw.Length >= 12 ? raw[..12] : raw.PadRight(12, 'A');
            result.Add($"{raw[..4]}-{raw.Substring(4, 4)}-{raw.Substring(8, 4)}");
        }
        return result;
    }

    public static string HashRecoveryCode(Guid userId, string? code)
    {
        var normalized = NormalizeRecoveryCode(code);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"jz2fa:{userId:D}:{normalized}"));
        return Convert.ToHexString(bytes);
    }

    public static string NormalizeRecoveryCode(string? code)
        => new string((code ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

    public static string HashLoginChallengeToken(string token)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("jz2fa-login:" + (token ?? string.Empty).Trim()));
        return Convert.ToHexString(bytes);
    }

    public static string NewLoginChallengeToken()
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return token.TrimEnd('=').Replace("+", "-", StringComparison.Ordinal).Replace("/", "_", StringComparison.Ordinal);
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        var a = Encoding.ASCII.GetBytes(left);
        var b = Encoding.ASCII.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    public static string ToBase32(byte[] bytes)
    {
        if (bytes.Length == 0) return string.Empty;
        var output = new StringBuilder((bytes.Length + 4) / 5 * 8);
        var buffer = (int)bytes[0];
        var next = 1;
        var bitsLeft = 8;
        while (bitsLeft > 0 || next < bytes.Length)
        {
            if (bitsLeft < 5)
            {
                if (next < bytes.Length)
                {
                    buffer <<= 8;
                    buffer |= bytes[next++] & 0xff;
                    bitsLeft += 8;
                }
                else
                {
                    var pad = 5 - bitsLeft;
                    buffer <<= pad;
                    bitsLeft += pad;
                }
            }

            var index = 0x1f & (buffer >> (bitsLeft - 5));
            bitsLeft -= 5;
            output.Append(Alphabet[index]);
        }
        return output.ToString();
    }

    public static byte[] FromBase32(string base32)
    {
        var input = new string((base32 ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (input.Length == 0) return Array.Empty<byte>();

        var output = new List<byte>(input.Length * 5 / 8);
        var buffer = 0;
        var bitsLeft = 0;
        foreach (var c in input)
        {
            var value = Alphabet.IndexOf(c);
            if (value < 0)
                throw new FormatException("Invalid base32 character.");
            buffer <<= 5;
            buffer |= value;
            bitsLeft += 5;
            if (bitsLeft >= 8)
            {
                output.Add((byte)(buffer >> (bitsLeft - 8)));
                bitsLeft -= 8;
            }
        }
        return output.ToArray();
    }
}
