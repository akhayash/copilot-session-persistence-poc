namespace CopilotSessionPersistencePoc.Diagnostics;

public sealed record SessionFsEntryInfo(
    string Path,
    string PathCategory,
    string Kind,
    long SizeBytes,
    DateTimeOffset BirthTime,
    DateTimeOffset ModifiedTime,
    long Version);

public sealed record SessionFsStorageEvidence(
    string Backend,
    string DatabasePath,
    bool DatabaseFileExists,
    long DatabaseSizeBytes,
    string HostSessionDirectory,
    bool HostSessionDirectoryExists,
    bool IndividualSessionFilesDetected,
    string Conclusion);

public sealed record SessionFsDiagnosticsSnapshot(
    string SessionId,
    int NodeCount,
    int FileCount,
    int DirectoryCount,
    long ContentBytes,
    int EventCount,
    DateTimeOffset? LastModifiedTime,
    SessionFsStorageEvidence Storage,
    IReadOnlyList<SessionFsEntryInfo> Entries);

public sealed record SessionFsEntryDetails(
    SessionFsEntryInfo Entry,
    string? Content,
    bool ContentTruncated,
    int OriginalCharacterCount,
    string StorageTable,
    string SessionKey,
    string PathKey);
