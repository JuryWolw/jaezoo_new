namespace JaeZoo.Server.Models.Files;

public enum FileScanStatus
{
    NotScanned = 0,
    MetadataChecked = 1,
    Clean = 2,
    Suspicious = 3,
    Blocked = 4
}
