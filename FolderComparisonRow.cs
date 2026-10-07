namespace FileSync
{
    internal sealed record FolderComparisonRow
    {
        public required string RelativePath { get; init; }
        public string? FullPath { get; init; }
        public bool IsFolder { get; init; }
        public long? SizeBytes { get; init; }
        public DateTime? LastWriteTimeUtc { get; init; }
        public bool CanCompare { get; init; } = true;
        public string Status { get; init; } = "Not compared";
        public bool IsMismatch { get; init; }
        public string? Message { get; init; }
        public string EntryType => FullPath is null ? "—" : IsFolder ? "Folder" : "File";
        public string SizeDisplay => SizeBytes?.ToString("N0") ?? "—";
        public string ModifiedDisplay => LastWriteTimeUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fffffff") ?? "—";
    }
}
