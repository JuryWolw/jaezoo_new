namespace JaeZoo.Server.Models.Files;

public enum FileScanStatus
{
    NotScanned = 0,
    MetadataChecked = 1,
    Clean = 2,
    Suspicious = 3,
    Blocked = 4,
    Pending = 5,
    Failed = 6,

    /// <summary>Encrypted on the client (MLS attachment key). Content is opaque: no antivirus scan possible.</summary>
    ClientEncrypted = 7
}
