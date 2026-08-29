using System.Net;
using System.Net.Http.Json;
using System.Text;
using Azure.Core;
using CopilotSessionPersistencePoc.Execution;
using Microsoft.Extensions.Options;

namespace CopilotSessionPersistencePoc.Tests;

public sealed class PresentationSessionsClientTests
{
    [Fact]
    public async Task CustomWorkerCallsUseEntraTokenAndIdentifier()
    {
        var requests = new List<(HttpMethod Method, Uri Uri, string? Authorization)>();
        var handler = new DelegateHandler(async request =>
        {
            requests.Add((
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization?.ToString()));
            if (request.Method == HttpMethod.Post
                && request.RequestUri!.AbsolutePath.EndsWith(
                    "/presentations",
                    StringComparison.Ordinal))
            {
                PresentationWorkerRequest? body =
                    await request.Content!.ReadFromJsonAsync<PresentationWorkerRequest>();
                Assert.Equal("deck.pptx", body!.FileName);
                return JsonResponse(
                    """
                    {
                      "validationPassed": true,
                      "slideCount": 2,
                      "files": [
                        {
                          "fileName": "deck.pptx",
                          "contentType": "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                          "sizeBytes": 4,
                          "sha256": "abcd"
                        }
                      ]
                    }
                    """);
            }

            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent("pptx"u8.ToArray()),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });
        AzurePresentationSessionsClient client = CreateClient(handler);
        var workerRequest = new PresentationWorkerRequest(
            "deck.pptx",
            "Title",
            null,
            "Audience",
            [new PresentationSlide("Result", "Passed", null)]);

        PresentationWorkerManifest manifest = await client.CreatePresentationAsync(
            "server-only-id",
            workerRequest,
            default);
        BinaryData content = await client.DownloadArtifactAsync(
            "server-only-id",
            "deck.pptx",
            default);
        await client.StopSessionAsync("server-only-id", default);

        Assert.True(manifest.ValidationPassed);
        Assert.Equal(2, manifest.SlideCount);
        Assert.Equal("pptx", content.ToString());
        Assert.Equal(3, requests.Count);
        Assert.All(
            requests,
            request =>
            {
                Assert.Equal("Bearer test-token", request.Authorization);
                Assert.Contains(
                    "identifier=server-only-id",
                    request.Uri.Query,
                    StringComparison.Ordinal);
                Assert.Contains(
                    "api-version=2025-02-02-preview",
                    request.Uri.Query,
                    StringComparison.Ordinal);
            });
        Assert.EndsWith(
            "/.management/stopSession",
            requests[2].Uri.AbsolutePath,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task WorkerFailureIsNotReturnedAsSuccess()
    {
        var handler = new DelegateHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("render failed"),
            }));
        AzurePresentationSessionsClient client = CreateClient(handler);

        PresentationSessionsException exception =
            await Assert.ThrowsAsync<PresentationSessionsException>(() =>
                client.CreatePresentationAsync(
                    "server-only-id",
                    new PresentationWorkerRequest(
                        "deck.pptx",
                        "Title",
                        null,
                        "Test audience",
                        [new PresentationSlide("Result", "Passed", null)]),
                    default));

        Assert.Contains("HTTP 502", exception.Message, StringComparison.Ordinal);
        Assert.Contains("render failed", exception.Message, StringComparison.Ordinal);
    }

    private static AzurePresentationSessionsClient CreateClient(
        HttpMessageHandler handler) =>
        new(
            new HttpClient(handler),
            new TestTokenCredential(),
            Options.Create(new PresentationSessionsOptions
            {
                Enabled = true,
                PoolManagementEndpoint = new Uri(
                    "https://presentation.example.azurecontainerapps.io/"),
            }));

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };

    private sealed class DelegateHandler(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            handler(request);
    }

    private sealed class TestTokenCredential : TokenCredential
    {
        public override AccessToken GetToken(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            new("test-token", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
