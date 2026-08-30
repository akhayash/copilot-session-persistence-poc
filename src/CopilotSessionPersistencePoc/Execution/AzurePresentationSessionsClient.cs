using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Microsoft.Extensions.Options;

namespace CopilotSessionPersistencePoc.Execution;

public sealed class AzurePresentationSessionsClient(
    HttpClient httpClient,
    TokenCredential credential,
    IOptions<PresentationSessionsOptions> options)
    : IPresentationSessionsClient
{
    private const string TokenScope = "https://dynamicsessions.io/.default";
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);
    private readonly PresentationSessionsOptions settings = options.Value;

    public async Task<PresentationWorkerManifest> CreatePresentationAsync(
        string identifier,
        PresentationWorkerRequest request,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage message = await CreateRequestAsync(
            HttpMethod.Post,
            "presentations",
            identifier,
            cancellationToken);
        message.Content = new StringContent(
            JsonSerializer.Serialize(request, JsonOptions),
            Encoding.UTF8,
            "application/json");
        using HttpResponseMessage response = await httpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(response, "create a presentation", cancellationToken);
        WorkerManifestResponse? manifest =
            await response.Content.ReadFromJsonAsync<WorkerManifestResponse>(
                JsonOptions,
                cancellationToken);
        if (manifest is null)
        {
            throw new PresentationSessionsException(
                "The presentation worker returned an empty manifest.");
        }

        return new PresentationWorkerManifest(
            manifest.ValidationPassed,
            manifest.SlideCount,
            manifest.Files?
                .Select(static file => new PresentationWorkerFile(
                    file.FileName ?? string.Empty,
                    file.ContentType ?? "application/octet-stream",
                    file.SizeBytes,
                    file.Sha256 ?? string.Empty))
                .ToArray() ?? []);
    }

    public async Task<BinaryData> DownloadArtifactAsync(
        string identifier,
        string fileName,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = await CreateRequestAsync(
            HttpMethod.Get,
            $"artifacts/{Uri.EscapeDataString(fileName)}",
            identifier,
            cancellationToken);
        using HttpResponseMessage response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(response, "download a presentation artifact", cancellationToken);
        byte[] content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (content.LongLength > settings.MaximumOutputBytes)
        {
            throw new PresentationSessionsException(
                "The presentation worker response exceeds the output size limit.");
        }

        return BinaryData.FromBytes(content);
    }

    public async Task<PresentationExecResult> ExecuteAsync(
        string identifier,
        string command,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage message = await CreateRequestAsync(
            HttpMethod.Post,
            "exec",
            identifier,
            cancellationToken);
        message.Content = JsonContent.Create(new
        {
            command,
            timeoutSeconds,
        });
        using HttpResponseMessage response = await httpClient.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(response, "execute a presentation command", cancellationToken);
        WorkerExecResponse? result =
            await response.Content.ReadFromJsonAsync<WorkerExecResponse>(
                JsonOptions,
                cancellationToken);
        if (result is null)
        {
            throw new PresentationSessionsException(
                "The presentation worker returned an empty execution result.");
        }

        return new PresentationExecResult(
            result.ExitCode,
            result.Stdout ?? string.Empty,
            result.Stderr ?? string.Empty,
            result.StdoutTruncated,
            result.StderrTruncated);
    }

    public async Task<IReadOnlyList<PresentationWorkspaceFile>> ListFilesAsync(
        string identifier,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = await CreateRequestAsync(
            HttpMethod.Get,
            "files",
            identifier,
            cancellationToken);
        using HttpResponseMessage response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(response, "list presentation workspace files", cancellationToken);
        WorkerFilesResponse? result =
            await response.Content.ReadFromJsonAsync<WorkerFilesResponse>(
                JsonOptions,
                cancellationToken);
        return result?.Files?
            .Select(static file => new PresentationWorkspaceFile(
                file.Path ?? string.Empty,
                file.SizeBytes,
                file.Sha256 ?? string.Empty))
            .ToArray() ?? [];
    }

    public async Task<PresentationWorkspaceFile> WriteFileAsync(
        string identifier,
        string path,
        BinaryData content,
        CancellationToken cancellationToken)
    {
        EnsureContentSize(content);
        using HttpRequestMessage request = await CreateRequestAsync(
            HttpMethod.Put,
            $"files/{EscapePath(path)}",
            identifier,
            cancellationToken);
        request.Content = JsonContent.Create(new
        {
            encoding = "base64",
            data = Convert.ToBase64String(content.ToArray()),
        });
        using HttpResponseMessage response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(response, "write a presentation workspace file", cancellationToken);
        WorkerFileResponse? result =
            await response.Content.ReadFromJsonAsync<WorkerFileResponse>(
                JsonOptions,
                cancellationToken);
        if (result?.Path is null)
        {
            throw new PresentationSessionsException(
                "The presentation worker returned an empty file result.");
        }

        return new PresentationWorkspaceFile(
            result.Path,
            result.SizeBytes,
            result.Sha256 ?? string.Empty);
    }

    public async Task<BinaryData> ReadFileAsync(
        string identifier,
        string path,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = await CreateRequestAsync(
            HttpMethod.Get,
            $"files/{EscapePath(path)}",
            identifier,
            cancellationToken);
        using HttpResponseMessage response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(response, "read a presentation workspace file", cancellationToken);
        byte[] content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (content.LongLength > settings.MaximumOutputBytes)
        {
            throw new PresentationSessionsException(
                "The presentation workspace file exceeds the output size limit.");
        }

        return BinaryData.FromBytes(content);
    }

    public async Task DeleteFileAsync(
        string identifier,
        string path,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = await CreateRequestAsync(
            HttpMethod.Delete,
            $"files/{EscapePath(path)}",
            identifier,
            cancellationToken);
        using HttpResponseMessage response = await httpClient.SendAsync(
            request,
            cancellationToken);
        await EnsureSuccessAsync(response, "delete a presentation workspace file", cancellationToken);
    }

    public async Task<PresentationRenderResult> RenderAsync(
        string identifier,
        string path,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = await CreateRequestAsync(
            HttpMethod.Post,
            "render",
            identifier,
            cancellationToken);
        request.Content = JsonContent.Create(new { path });
        using HttpResponseMessage response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        await EnsureSuccessAsync(response, "render a presentation workspace file", cancellationToken);
        WorkerRenderResponse? result =
            await response.Content.ReadFromJsonAsync<WorkerRenderResponse>(
                JsonOptions,
                cancellationToken);
        if (result is null)
        {
            throw new PresentationSessionsException(
                "The presentation worker returned an empty render result.");
        }

        PresentationRenderImage[] images = result.Images?
            .Select(static image => new PresentationRenderImage(
                image.SlideNumber,
                image.MimeType ?? "image/png",
                BinaryData.FromBytes(Convert.FromBase64String(image.Data ?? string.Empty))))
            .ToArray() ?? [];
        if (images.Sum(static image => image.Content.ToMemory().Length)
            > settings.MaximumOutputBytes)
        {
            throw new PresentationSessionsException(
                "The presentation previews exceed the output size limit.");
        }

        return new PresentationRenderResult(
            result.Validation?.Passed == true,
            result.SlideCount,
            images);
    }

    public async Task StopSessionAsync(
        string identifier,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = await CreateRequestAsync(
            HttpMethod.Post,
            ".management/stopSession",
            identifier,
            cancellationToken);
        using HttpResponseMessage response =
            await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, "stop a presentation session", cancellationToken);
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(
        HttpMethod method,
        string relativePath,
        string identifier,
        CancellationToken cancellationToken)
    {
        Uri endpoint = settings.PoolManagementEndpoint
            ?? throw new InvalidOperationException(
                "PresentationSessions:PoolManagementEndpoint is required.");
        var normalizedEndpoint = new Uri($"{endpoint.AbsoluteUri.TrimEnd('/')}/");
        var uri = new UriBuilder(new Uri(normalizedEndpoint, relativePath))
        {
            Query =
                $"api-version={Uri.EscapeDataString(settings.ApiVersion)}"
                + $"&identifier={Uri.EscapeDataString(identifier)}",
        }.Uri;
        AccessToken token = await credential.GetTokenAsync(
            new TokenRequestContext([TokenScope]),
            cancellationToken);
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return request;
    }

    private void EnsureContentSize(BinaryData content)
    {
        if (content.ToMemory().Length > settings.MaximumOutputBytes)
        {
            throw new PresentationSessionsException(
                "The presentation workspace file exceeds the output size limit.");
        }
    }

    private static string EscapePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Contains('\\', StringComparison.Ordinal)
            || path[0] == '/')
        {
            throw new ArgumentException(
                "Workspace paths must be relative and use POSIX separators.",
                nameof(path));
        }

        string[] segments = path.Split('/', StringSplitOptions.None);
        if (segments.Any(static segment =>
                string.IsNullOrEmpty(segment) || segment is "." or ".."))
        {
            throw new ArgumentException(
                "Workspace paths cannot contain empty, '.' or '..' segments.",
                nameof(path));
        }

        return string.Join('/', segments.Select(Uri.EscapeDataString));
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

        throw new PresentationSessionsException(
            $"Failed to {operation}: HTTP {(int)response.StatusCode}. {detail}");
    }

    private sealed record WorkerManifestResponse(
        [property: JsonPropertyName("validationPassed")] bool ValidationPassed,
        [property: JsonPropertyName("slideCount")] int SlideCount,
        [property: JsonPropertyName("files")] WorkerFileResponse[]? Files);

    private sealed record WorkerFileResponse(
        [property: JsonPropertyName("fileName")] string? FileName,
        [property: JsonPropertyName("path")] string? Path,
        [property: JsonPropertyName("contentType")] string? ContentType,
        [property: JsonPropertyName("sizeBytes")] long SizeBytes,
        [property: JsonPropertyName("sha256")] string? Sha256);

    private sealed record WorkerExecResponse(
        [property: JsonPropertyName("exitCode")] int ExitCode,
        [property: JsonPropertyName("stdout")] string? Stdout,
        [property: JsonPropertyName("stderr")] string? Stderr,
        [property: JsonPropertyName("stdoutTruncated")] bool StdoutTruncated,
        [property: JsonPropertyName("stderrTruncated")] bool StderrTruncated);

    private sealed record WorkerFilesResponse(
        [property: JsonPropertyName("files")] WorkerFileResponse[]? Files);

    private sealed record WorkerRenderResponse(
        [property: JsonPropertyName("validation")] WorkerValidationResponse? Validation,
        [property: JsonPropertyName("slideCount")] int SlideCount,
        [property: JsonPropertyName("images")] WorkerImageResponse[]? Images);

    private sealed record WorkerValidationResponse(
        [property: JsonPropertyName("passed")] bool Passed);

    private sealed record WorkerImageResponse(
        [property: JsonPropertyName("slideNumber")] int SlideNumber,
        [property: JsonPropertyName("mimeType")] string? MimeType,
        [property: JsonPropertyName("data")] string? Data);
}
