using System.ComponentModel.DataAnnotations;

namespace JaeZoo.Server.Models;

public class GroupHistoryKeyPackage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid GroupChatId { get; set; }

    public Guid SenderUserId { get; set; }

    [MaxLength(128)]
    public string SenderDeviceId { get; set; } = string.Empty;

    [MaxLength(128)]
    public string SenderKeyId { get; set; } = string.Empty;

    public int SecurityEpoch { get; set; } = 1;

    public Guid ProviderUserId { get; set; }

    [MaxLength(128)]
    public string ProviderDeviceId { get; set; } = string.Empty;

    public Guid TargetUserId { get; set; }

    [MaxLength(128)]
    public string TargetDeviceId { get; set; } = string.Empty;

    [MaxLength(8192)]
    public string ProviderPublicKeyBase64 { get; set; } = string.Empty;

    [MaxLength(8192)]
    public string TargetPublicKeyBase64 { get; set; } = string.Empty;

    [MaxLength(256)]
    public string NonceBase64 { get; set; } = string.Empty;

    [MaxLength(8192)]
    public string CiphertextBase64 { get; set; } = string.Empty;

    [MaxLength(256)]
    public string TagBase64 { get; set; } = string.Empty;

    [MaxLength(96)]
    public string Algorithm { get; set; } = "JZ-GROUP-HISTORY-KEY-P256-AESGCM-v1";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? DeliveredAt { get; set; }
}
