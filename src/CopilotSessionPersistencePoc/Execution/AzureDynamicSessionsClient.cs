using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Microsoft.Extensions.Options;

namespace CopilotSessionPersistencePoc.Execution;

public sealed class AzureDynamicSessionsClient(
    HttpClient httpClient,
    TokenCredential credential,
    IOptions<DynamicSessionsOptions> options)
    : IDynamicSessionsClient
{
    private const string TokenScope = "https://dynamicsessions.io/.default";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly DynamicSessionsOptions settings = options.Value;

    public async Task UploadFileAsync(
        string identifier,
        string fileName,
        string contentType,
        BinaryData content,
        CancellationToken cancellationToken)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content.ToArray());
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        form.Add(file, "file", fileName);
        using HttpRequestMessage request = await CreateRequestAsync(
            HttpMethod.Post,
            "files",
            identifier,
            cancellationToken);
        request.Content = form;
        using HttpResponseMessage response =
            await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "upload a sandbox file", cancellationToken);
    }

    public async Task<DynamicSessionExecutionResult> ExecuteCodeAsync(
        string identifier,
        string code,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = await CreateRequestAsync(
            HttpMethod.Post,
            "executions",
            identifier,
            cancellationToken);
        request.Content = JsonContent.Create(new
        {
            codeInputType = "Inline",
            executionType = "Synchronous",
            code,
            timeoutInSeconds = settings.ExecutionTimeoutSeconds,
            outputStreamsMaxLength = settings.MaximumCapturedCharacters,
        });
        using HttpResponseMessage response =
            await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "execute Python", cancellationToken);
        ExecutionResponse? execution = await response.Content.ReadFromJsonAsync<ExecutionResponse>(
            JsonOptions,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(execution?.Status))
        {
            throw new DynamicSessionsException(
                "Dynamic Sessions returned an execution response without a status.");
        }

        ExecutionResultPayload? result = execution.Result;
        if (result is null)
        {
            if (execution.Status.Equals("Succeeded", StringComparison.OrdinalIgnoreCase))
            {
                throw new DynamicSessionsException(
                    "Dynamic Sessions returned a successful response without a result.");
            }

            return new DynamicSessionExecutionResult(
                execution.Status,
                string.Empty,
                string.Empty,
                string.Empty,
                0);
        }

        return new DynamicSessionExecutionResult(
            execution.Status,
            result.Stdout ?? string.Empty,
            result.Stderr ?? string.Empty,
            result.ExecutionResult is { } executionResult
                ? executionResult.ValueKind == JsonValueKind.String
                    ? executionResult.GetString() ?? string.Empty
                    : executionResult.GetRawText()
                : string.Empty,
            result.ExecutionTimeInMilliseconds);
    }

    public async Task<IReadOnlyList<DynamicSessionFile>> ListFilesAsync(
        string identifier,
        CancellationToken cancellationToken)
    {
        var files = new List<DynamicSessionFile>();
        string? nextLink = null;
        do
        {
            using HttpRequestMessage request = nextLink is null
                ? await CreateRequestAsync(
                    HttpMethod.Get,
                    "files",
                    identifier,
                    cancellationToken)
                : await CreateContinuationRequestAsync(nextLink, cancellationToken);
            using HttpResponseMessage response =
                await httpClient.SendAsync(request, cancellationToken);
            await EnsureSuccessAsync(response, "list sandbox files", cancellationToken);
            FileEnvelope? envelope = await response.Content.ReadFromJsonAsync<FileEnvelope>(
                JsonOptions,
                cancellationToken);
            files.AddRange(envelope?.Value?
                .Where(static item => item.Type?.Equals(
                    "File",
                    StringComparison.OrdinalIgnoreCase) is true
                    && item.Name is { Length: > 0 }
                    && string.IsNullOrEmpty(item.Directory))
                .Select(static item => new DynamicSessionFile(
                    item.Name!,
                    item.SizeInBytes,
                    item.LastModifiedAt)) ?? []);
            if (files.Count > settings.MaximumInputFiles + settings.MaximumOutputFiles)
            {
                throw new DynamicSessionsException(
                    "The sandbox returned more files than the configured limit.");
            }

            nextLink = envelope?.NextLink;
        }
        while (!string.IsNullOrWhiteSpace(nextLink));

        return files;
    }

    public async Task<BinaryData> DownloadFileAsync(
        string identifier,
        string fileName,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = await CreateRequestAsync(
            HttpMethod.Get,
            $"files/{Uri.EscapeDataString(fileName)}/content",
            identifier,
            cancellationToken);
        using HttpResponseMessage response =
            await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "download a sandbox file", cancellationToken);
        return BinaryData.FromBytes(
            await response.Content.ReadAsByteArrayAsync(cancellationToken));
    }

    public async Task DeleteSessionAsync(
        string identifier,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = await CreateRequestAsync(
            HttpMethod.Delete,
            "session",
            identifier,
            cancellationToken);
        using HttpResponseMessage response =
            await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "delete a sandbox session", cancellationToken);
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method,
        string relativePath,
        string identifier,
        CancellationToken cancellationToken)
    {
        Uri endpoint = settings.PoolManagementEndpoint
            ?? throw new InvalidOperationException(
                "DynamicSessions:PoolManagementEndpoint is required.");
        var normalizedEndpoint = new Uri($"{endpoint.AbsoluteUri.TrimEnd('/')}/");
        var uri = new UriBuilder(new Uri(normalizedEndpoint, relativePath))
        {
            Query =
                $"api-version={Uri.EscapeDataString(settings.ApiVersion)}"
                + $"&identifier={Uri.EscapeDataString(identifier)}",
        }.Uri;
        return await CreateAuthorizedRequestAsync(method, uri, cancellationToken);
    }

    private async Task<HttpRequestMessage> CreateContinuationRequestAsync(
        string nextLink,
        CancellationToken cancellationToken)
    {
        Uri endpoint = settings.PoolManagementEndpoint
            ?? throw new InvalidOperationException(
                "DynamicSessions:PoolManagementEndpoint is required.");
        var normalizedEndpoint = new Uri($"{endpoint.AbsoluteUri.TrimEnd('/')}/");
        var continuation = new Uri(normalizedEndpoint, nextLink);
        if (!continuation.Scheme.Equals(normalizedEndpoint.Scheme, StringComparison.OrdinalIgnoreCase)
            || !continuation.Host.Equals(normalizedEndpoint.Host, StringComparison.OrdinalIgnoreCase)
            || continuation.Port != normalizedEndpoint.Port
            || !continuation.AbsolutePath.StartsWith(
                normalizedEndpoint.AbsolutePath,
                StringComparison.Ordinal))
        {
            throw new DynamicSessionsException(
                "Dynamic Sessions returned an invalid file-list continuation URL.");
        }

        return await CreateAuthorizedRequestAsync(
            HttpMethod.Get,
            continuation,
            cancellationToken);
    }

    private async Task<HttpRequestMessage> CreateAuthorizedRequestAsync(
        HttpMethod method,
        Uri uri,
        CancellationToken cancellationToken)
    {
        AccessToken token = await credential.GetTokenAsync(
            new TokenRequestContext([TokenScope]),
            cancellationToken);
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return request;
    }

    private static async Task EnsureSuccessAsync(
        HttpResponseMessage response,
        string operation,
        CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        string detail = await response.Content.ReadAsStringAsync(cancellationToken);
        if (detail.Length > 2048)
        {
            detail = detail[..2048];
        }

        throw new DynamicSessionsException(
            $"Failed to {operation}: HTTP {(int)response.StatusCode}. {detail}");
    }

    private sealed record ExecutionResponse(
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("result")] ExecutionResultPayload? Result);

    private sealed record ExecutionResultPayload(
        [property: JsonPropertyName("stdout")] string? Stdout,
        [property: JsonPropertyName("stderr")] string? Stderr,
        [property: JsonPropertyName("executionResult")] JsonElement? ExecutionResult,
        [property: JsonPropertyName("executionTimeInMilliseconds")]
        long ExecutionTimeInMilliseconds);

    private sealed record FileEnvelope(
        [property: JsonPropertyName("value")] FileItem[]? Value,
        [property: JsonPropertyName("nextLink")] string? NextLink);

    private sealed record FileItem(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("directory")] string? Directory,
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("sizeInBytes")] long SizeInBytes,
        [property: JsonPropertyName("lastModifiedAt")] DateTimeOffset? LastModifiedAt);
}
