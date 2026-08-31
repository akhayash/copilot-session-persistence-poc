using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CopilotSessionPersistencePoc.ArtifactStorage;
using CopilotSessionPersistencePoc.SessionFs;
using Microsoft.Extensions.Options;

namespace CopilotSessionPersistencePoc.Execution;

public sealed class PresentationWorkspaceCoordinator(
    IPresentationSessionsClient sessions,
    AzureBlobSessionFsStore sessionFs,
    IArtifactStore artifacts,
    IOptions<PresentationSessionsOptions> options)
{
    private const string PresentationRoot = "/presentation";
    private const string PresentationQaRoot = "/internal/presentation-qa";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly PresentationSessionsOptions settings = options.Value;

    public async Task<PresentationExecResult> ExecuteAsync(
        string sessionId,
        string deckId,
        string command,
        CancellationToken cancellationToken)
    {
        string identifier = GetIdentifier(sessionId, deckId);
        await MaterializeAsync(sessionId, deckId, identifier, cancellationToken);
        PresentationExecResult result = await sessions.ExecuteAsync(
            identifier,
            command,
            Math.Min(settings.RequestTimeoutSeconds, 90),
            cancellationToken);
        await CommitAsync(sessionId, deckId, identifier, cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<PresentationWorkspaceFile>> ListFilesAsync(
        string sessionId,
        string deckId,
        CancellationToken cancellationToken)
    {
        string identifier = GetIdentifier(sessionId, deckId);
        await MaterializeAsync(sessionId, deckId, identifier, cancellationToken);
        return await sessions.ListFilesAsync(identifier, cancellationToken);
    }

    public async Task<string> ReadTextAsync(
        string sessionId,
        string deckId,
        string path,
        CancellationToken cancellationToken)
    {
        string identifier = GetIdentifier(sessionId, deckId);
        await MaterializeAsync(sessionId, deckId, identifier, cancellationToken);
        BinaryData content = await sessions.ReadFileAsync(identifier, path, cancellationToken);
        return Encoding.UTF8.GetString(content.ToArray());
    }

    public async Task<PresentationWorkspaceFile> WriteTextAsync(
        string sessionId,
        string deckId,
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        string identifier = GetIdentifier(sessionId, deckId);
        await MaterializeAsync(sessionId, deckId, identifier, cancellationToken);
        PresentationWorkspaceFile result = await sessions.WriteFileAsync(
            identifier,
            path,
            BinaryData.FromString(content),
            cancellationToken);
        await CommitAsync(sessionId, deckId, identifier, cancellationToken);
        return result;
    }

    public async Task<PresentationRenderResult> RenderAsync(
        string sessionId,
        string deckId,
        string path,
        CancellationToken cancellationToken)
    {
        string identifier = GetIdentifier(sessionId, deckId);
        await MaterializeAsync(sessionId, deckId, identifier, cancellationToken);
        BinaryData content = await sessions.ReadFileAsync(
            identifier,
            path,
            cancellationToken);
        string sha256BeforeRender = ComputeContentHash(content, path);
        PresentationRenderResult result =
            await sessions.RenderAsync(identifier, path, cancellationToken);
        if (result.ValidationPassed && result.Images.Count > 0)
        {
            BinaryData contentAfterRender = await sessions.ReadFileAsync(
                identifier,
                path,
                cancellationToken);
            string sha256AfterRender = ComputeContentHash(contentAfterRender, path);
            if (!string.Equals(
                sha256BeforeRender,
                sha256AfterRender,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The presentation changed while it was being previewed. "
                    + "Call pptx_preview again before publishing.");
            }

            await RecordPreviewAsync(
                sessionId,
                deckId,
                path,
                sha256AfterRender,
                cancellationToken);
        }

        return result;
    }

    public async Task<ArtifactInfo> PublishAsync(
        string sessionId,
        string deckId,
        string path,
        CancellationToken cancellationToken)
    {
        string identifier = GetIdentifier(sessionId, deckId);
        await MaterializeAsync(sessionId, deckId, identifier, cancellationToken);
        BinaryData content = await sessions.ReadFileAsync(identifier, path, cancellationToken);
        string sha256 = ComputeContentHash(content, path);
        PresentationQaState qaState =
            await ReadQaStateAsync(sessionId, deckId, cancellationToken);
        if (qaState.IsPublished(path, sha256))
        {
            ArtifactContent? existing = await artifacts.GetAsync(
                sessionId,
                qaState.PublishedArtifactId!,
                qaState.PublishedFileName!,
                cancellationToken);
            if (existing is not null)
            {
                return existing.Info;
            }
        }

        qaState.EnsureCanPublish(path, sha256);

        PresentationRenderResult validation =
            await sessions.RenderAsync(identifier, path, cancellationToken);
        if (!validation.ValidationPassed)
        {
            throw new InvalidDataException("Presentation validation failed.");
        }

        BinaryData validatedContent = await sessions.ReadFileAsync(
            identifier,
            path,
            cancellationToken);
        string validatedSha256 = ComputeContentHash(validatedContent, path);
        if (!string.Equals(sha256, validatedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The presentation changed while it was being validated for publishing. "
                + "Call pptx_preview again before publishing.");
        }

        string fileName = Path.GetFileName(path);
        string artifactId = $"pptx-{deckId}-{sha256[..16]}";
        await MutateQaStateAsync(
            sessionId,
            deckId,
            current =>
            {
                if (!current.IsPublished(path, sha256))
                {
                    current.EnsureCanPublish(path, sha256);
                    current.MarkPublished(path, sha256, artifactId, fileName);
                }
            },
            cancellationToken);
        return await artifacts.PutAsync(
            sessionId,
            artifactId,
            fileName,
            "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            validatedContent,
            cancellationToken);
    }

    public string GetIdentifier(string sessionId, string deckId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ValidateDeckId(deckId);
        byte[] input = Encoding.UTF8.GetBytes($"{sessionId}\n{deckId}");
        byte[] digest = string.IsNullOrWhiteSpace(settings.IdentifierKey)
            ? SHA256.HashData(input)
            : HMACSHA256.HashData(Encoding.UTF8.GetBytes(settings.IdentifierKey), input);
        return Convert.ToHexStringLower(digest);
    }

    private async Task MaterializeAsync(
        string sessionId,
        string deckId,
        string identifier,
        CancellationToken cancellationToken)
    {
        string prefix = DeckPrefix(deckId);
        AzureSessionFsState state = await sessionFs.ReadAsync(sessionId, cancellationToken);
        PresentationWorkspaceFile[] remote =
            [.. await sessions.ListFilesAsync(identifier, cancellationToken)];
        Dictionary<string, PresentationWorkspaceFile> remoteByPath = remote.ToDictionary(
            static file => file.Path,
            StringComparer.Ordinal);
        var persistedPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach ((string nodePath, AzureSessionFsNode node) in state.Nodes)
        {
            if (node.Kind != "file"
                || !nodePath.StartsWith($"{prefix}/", StringComparison.Ordinal)
                || node.Content is null)
            {
                continue;
            }

            string relativePath = nodePath[(prefix.Length + 1)..];
            persistedPaths.Add(relativePath);
            WorkspaceFileReference? reference;
            try
            {
                reference = JsonSerializer.Deserialize<WorkspaceFileReference>(
                    node.Content,
                    JsonOptions);
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException(
                    $"Presentation workspace file '{relativePath}' has an invalid manifest.",
                    exception);
            }
            if (reference is null)
            {
                throw new InvalidDataException(
                    $"Presentation workspace file '{relativePath}' has an empty manifest.");
            }

            if (!remoteByPath.TryGetValue(relativePath, out PresentationWorkspaceFile? current)
                || !reference.Sha256.Equals(
                    current.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                ArtifactContent? artifact = await artifacts.GetAsync(
                    sessionId,
                    reference.ArtifactId,
                    reference.FileName,
                    cancellationToken);
                if (artifact is null
                    || !reference.Sha256.Equals(
                        artifact.Info.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        $"Presentation workspace blob for '{relativePath}' is missing or corrupt.");
                }

                await sessions.WriteFileAsync(
                    identifier,
                    relativePath,
                    artifact.Content,
                    cancellationToken);
            }
        }

        foreach (PresentationWorkspaceFile stale in remote
            .Where(file => !persistedPaths.Contains(file.Path)))
        {
            await sessions.DeleteFileAsync(identifier, stale.Path, cancellationToken);
        }
    }

    private async Task CommitAsync(
        string sessionId,
        string deckId,
        string identifier,
        CancellationToken cancellationToken)
    {
        string prefix = DeckPrefix(deckId);
        AzureSessionFsState previousState =
            await sessionFs.ReadAsync(sessionId, cancellationToken);
        HashSet<string> previousArtifactIds = WorkspaceReferences(previousState, prefix)
            .Select(static reference => reference.ArtifactId)
            .ToHashSet(StringComparer.Ordinal);
        PresentationWorkspaceFile[] files =
            [.. await sessions.ListFilesAsync(identifier, cancellationToken)];
        if (files.Length > settings.MaximumFiles)
        {
            throw new IOException(
                $"Presentation workspace contains more than {settings.MaximumFiles} files.");
        }

        var references = new Dictionary<string, WorkspaceFileReference>(StringComparer.Ordinal);
        long totalBytes = 0;
        foreach (PresentationWorkspaceFile file in files)
        {
            totalBytes += file.SizeBytes;
            if (totalBytes > settings.MaximumOutputBytes)
            {
                throw new IOException("Presentation workspace exceeds the configured size limit.");
            }

            BinaryData content = await sessions.ReadFileAsync(
                identifier,
                file.Path,
                cancellationToken);
            string contentHash = Convert.ToHexStringLower(
                SHA256.HashData(content.ToMemory().Span));
            if (!contentHash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    $"Presentation workspace file '{file.Path}' hash did not match its manifest.");
            }

            string artifactId = $".workspace-{deckId}-{contentHash[..16]}";
            string storedFileName = Convert.ToHexStringLower(
                SHA256.HashData(Encoding.UTF8.GetBytes(file.Path)));
            ArtifactInfo stored = await artifacts.PutAsync(
                sessionId,
                artifactId,
                storedFileName,
                "application/octet-stream",
                content,
                cancellationToken);
            references[$"{prefix}/{file.Path}"] = new WorkspaceFileReference(
                artifactId,
                storedFileName,
                stored.Sha256,
                stored.SizeBytes);
        }

        await sessionFs.MutateAsync(
            sessionId,
            state =>
            {
                foreach (string path in state.Nodes.Keys
                    .Where(path => path.StartsWith($"{prefix}/", StringComparison.Ordinal))
                    .ToArray())
                {
                    state.Nodes.Remove(path);
                }

                DateTimeOffset now = DateTimeOffset.UtcNow;
                EnsureDirectory(state, PresentationRoot, now);
                EnsureDirectory(state, prefix, now);
                foreach ((string path, WorkspaceFileReference reference) in references)
                {
                    SessionFsPath normalized = SessionFsPath.Parse(path);
                    foreach (string ancestor in normalized.Ancestors())
                    {
                        EnsureDirectory(state, ancestor, now);
                    }

                    state.Nodes[path] = new AzureSessionFsNode
                    {
                        Kind = "file",
                        Content = JsonSerializer.Serialize(reference, JsonOptions),
                        Birthtime = now,
                        Mtime = now,
                        Version = 1,
                    };
                }
            },
            cancellationToken);

        HashSet<string> currentArtifactIds = references.Values
            .Select(static reference => reference.ArtifactId)
            .ToHashSet(StringComparer.Ordinal);
        foreach (string obsoleteArtifactId in previousArtifactIds.Except(currentArtifactIds))
        {
            await artifacts.DeleteAsync(
                sessionId,
                obsoleteArtifactId,
                cancellationToken);
        }
    }

    private static void EnsureDirectory(
        AzureSessionFsState state,
        string path,
        DateTimeOffset now)
    {
        if (path == "/" || state.Nodes.ContainsKey(path))
        {
            return;
        }

        state.Nodes[path] = new AzureSessionFsNode
        {
            Kind = "directory",
            Birthtime = now,
            Mtime = now,
            Version = 1,
        };
    }

    private static string DeckPrefix(string deckId)
    {
        ValidateDeckId(deckId);
        return $"{PresentationRoot}/{deckId}";
    }

    private async Task RecordPreviewAsync(
        string sessionId,
        string deckId,
        string path,
        string sha256,
        CancellationToken cancellationToken)
    {
        await MutateQaStateAsync(
            sessionId,
            deckId,
            state => state.RecordPreview(path, sha256, DateTimeOffset.UtcNow),
            cancellationToken);
    }

    private async Task<PresentationQaState> ReadQaStateAsync(
        string sessionId,
        string deckId,
        CancellationToken cancellationToken)
    {
        AzureSessionFsState state = await sessionFs.ReadAsync(sessionId, cancellationToken);
        return DeserializeQaState(state, sessionId, deckId);
    }

    private async Task MutateQaStateAsync(
        string sessionId,
        string deckId,
        Action<PresentationQaState> mutation,
        CancellationToken cancellationToken)
    {
        string path = QaStatePath(deckId);
        await sessionFs.MutateAsync(
            sessionId,
            state =>
            {
                PresentationQaState qaState =
                    DeserializeQaState(state, sessionId, deckId);
                mutation(qaState);
                string signature = SignQaState(sessionId, deckId, qaState);
                DateTimeOffset now = DateTimeOffset.UtcNow;
                EnsureDirectory(state, "/internal", now);
                EnsureDirectory(state, PresentationQaRoot, now);
                state.Nodes[path] = new AzureSessionFsNode
                {
                    Kind = "file",
                    Content = JsonSerializer.Serialize(
                        new QaStateEnvelope(qaState, signature),
                        JsonOptions),
                    Birthtime = state.Nodes.TryGetValue(path, out AzureSessionFsNode? existing)
                        ? existing.Birthtime
                        : now,
                    Mtime = now,
                    Version = (existing?.Version ?? 0) + 1,
                };
            },
            cancellationToken);
    }

    private PresentationQaState DeserializeQaState(
        AzureSessionFsState state,
        string sessionId,
        string deckId)
    {
        string path = QaStatePath(deckId);
        if (!state.Nodes.TryGetValue(path, out AzureSessionFsNode? node)
            || node.Kind != "file"
            || node.Content is null)
        {
            return new PresentationQaState();
        }

        try
        {
            QaStateEnvelope? envelope =
                JsonSerializer.Deserialize<QaStateEnvelope>(node.Content, JsonOptions);
            if (envelope is null
                || !VerifyQaSignature(sessionId, deckId, envelope.State, envelope.Signature))
            {
                return new PresentationQaState();
            }

            return envelope.State;
        }
        catch (JsonException)
        {
            return new PresentationQaState();
        }
    }

    private static string QaStatePath(string deckId)
    {
        ValidateDeckId(deckId);
        return $"{PresentationQaRoot}/{deckId}.json";
    }

    private string SignQaState(
        string sessionId,
        string deckId,
        PresentationQaState qaState)
    {
        byte[] payload = QaSignaturePayload(sessionId, deckId, qaState);
        return Convert.ToHexStringLower(
            HMACSHA256.HashData(GetQaSigningKey(), payload));
    }

    private bool VerifyQaSignature(
        string sessionId,
        string deckId,
        PresentationQaState qaState,
        string signature)
    {
        byte[] expected = HMACSHA256.HashData(
            GetQaSigningKey(),
            QaSignaturePayload(sessionId, deckId, qaState));
        try
        {
            byte[] actual = Convert.FromHexString(signature);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private byte[] GetQaSigningKey()
    {
        if (string.IsNullOrWhiteSpace(settings.IdentifierKey))
        {
            throw new InvalidOperationException(
                "PresentationSessions:IdentifierKey is required for signed QA state.");
        }

        return Encoding.UTF8.GetBytes(settings.IdentifierKey);
    }

    private static byte[] QaSignaturePayload(
        string sessionId,
        string deckId,
        PresentationQaState qaState) =>
        Encoding.UTF8.GetBytes(
            $"{sessionId}\n{deckId}\n"
            + JsonSerializer.Serialize(qaState, JsonOptions));

    private static string ComputeContentHash(BinaryData content, string path)
    {
        try
        {
            return PresentationContentHasher.Compute(content);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidOperationException(
                $"'{path}' is not a valid PPTX Open XML package. "
                + "Regenerate it and call pptx_preview again.",
                exception);
        }
    }

    private static void ValidateDeckId(string deckId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deckId);
        if (deckId.Length > 64
            || deckId.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new ArgumentException(
                "deckId must contain only ASCII letters, digits, '-' or '_' and be at most 64 characters.",
                nameof(deckId));
        }
    }

    private static IEnumerable<WorkspaceFileReference> WorkspaceReferences(
        AzureSessionFsState state,
        string prefix)
    {
        foreach ((string path, AzureSessionFsNode node) in state.Nodes)
        {
            if (node.Kind != "file"
                || !path.StartsWith($"{prefix}/", StringComparison.Ordinal)
                || node.Content is null)
            {
                continue;
            }

            WorkspaceFileReference? reference;
            try
            {
                reference = JsonSerializer.Deserialize<WorkspaceFileReference>(
                    node.Content,
                    JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (reference is not null)
            {
                yield return reference;
            }
        }
    }

    private sealed record WorkspaceFileReference(
        string ArtifactId,
        string FileName,
        string Sha256,
        long SizeBytes);

    private sealed record QaStateEnvelope(
        PresentationQaState State,
        string Signature);
}
