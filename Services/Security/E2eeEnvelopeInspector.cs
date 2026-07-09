using System.Text;

namespace JaeZoo.Server.Services.Security;

public readonly record struct E2eeEnvelopeInfo(int Version, string? Protocol);

public static class E2eeEnvelopeInspector
{
    public const string DirectPrefixV1 = "jze2ee1:";
    public const string DirectPrefixV2 = "jze2ee2:";
    public const string DirectPrefixV3 = "jze2ee3:";
    public const string GroupPrefixV1 = "jze2eeg1:";

    public static E2eeEnvelopeInfo InspectDirect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new E2eeEnvelopeInfo(0, null);
        if (text.StartsWith(DirectPrefixV3, StringComparison.Ordinal))
            return new E2eeEnvelopeInfo(3, DetectDirectRatchetProtocol(text));
        if (text.StartsWith(DirectPrefixV2, StringComparison.Ordinal)) return new E2eeEnvelopeInfo(2, "direct-static-ecdh-v2");
        if (text.StartsWith(DirectPrefixV1, StringComparison.Ordinal)) return new E2eeEnvelopeInfo(1, "direct-static-ecdh-v1");
        return new E2eeEnvelopeInfo(0, null);
    }

    public static E2eeEnvelopeInfo InspectGroup(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new E2eeEnvelopeInfo(0, null);
        if (text.StartsWith(GroupPrefixV1, StringComparison.Ordinal)) return new E2eeEnvelopeInfo(1, "group-content-key-v1");
        return new E2eeEnvelopeInfo(0, null);
    }

    private static string DetectDirectRatchetProtocol(string text)
    {
        try
        {
            var raw = text[DirectPrefixV3.Length..];
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(raw));
            if (json.Contains("double-ratchet-v3.3", StringComparison.OrdinalIgnoreCase)) return "direct-double-ratchet-v3.3";
            if (json.Contains("double-ratchet-v3.2", StringComparison.OrdinalIgnoreCase)) return "direct-double-ratchet-v3.2";
            if (json.Contains("X3DH", StringComparison.OrdinalIgnoreCase)) return "direct-x3dh-ratchet-v3.1";
            return "direct-ratchet-v3";
        }
        catch
        {
            return "direct-ratchet-v3";
        }
    }
}
