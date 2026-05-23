using JaeZoo.Server.Models.Files;

namespace JaeZoo.Server.Services.Files;

public sealed record FileInspectionResult(
    StoredFileKind Kind,
    string DetectedContentType,
    string SafeFileName,
    string Extension,
    string Sha256Hex,
    bool IsImage,
    bool IsVideo,
    bool IsAudio,
    bool IsPotentiallyDangerous,
    string? RiskNote,
    FileScanStatus ScanStatus
);
