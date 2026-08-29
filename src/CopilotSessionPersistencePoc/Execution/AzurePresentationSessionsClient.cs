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
        [property: JsonPropertyName("contentType")] string? ContentType,
        [property: JsonPropertyName("sizeBytes")] long SizeBytes,
        [property: JsonPropertyName("sha256")] string? Sha256);
}
