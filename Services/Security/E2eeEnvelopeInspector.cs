namespace JaeZoo.Server.Services.Security;

public readonly record struct E2eeEnvelopeInfo(int Version, string? Protocol);

public static class E2eeEnvelopeInspector
{
    public const string DirectPrefixV1 = "jze2ee1:";
    public const string DirectPrefixV2 = "jze2ee2:";
    public const string GroupPrefixV1 = "jze2eeg1:";

    public static E2eeEnvelopeInfo InspectDirect(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new E2eeEnvelopeInfo(0, null);
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
}
