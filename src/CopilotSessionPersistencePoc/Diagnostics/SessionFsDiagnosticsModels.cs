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
    string PersistenceMode,
    string ApplicationMetadataBackend,
    string SessionFsBackend,
    string SessionLockBackend,
    string ArtifactBackend,
    string Backend,
    string StorageLocation,
    bool StorageObjectExists,
    long StorageSizeBytes,
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
    string StorageObject,
    string SessionKey,
    string PathKey);
