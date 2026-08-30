using System.Net;
using System.Net.Http.Json;
using System.Text;
using Azure.Core;
using CopilotSessionPersistencePoc.Execution;
using GitHub.Copilot;
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

    [Fact]
    public async Task WorkspaceCallsRoundTripWorkerContracts()
    {
        var handler = new DelegateHandler(request =>
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post
                && path.EndsWith("/exec", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(
                    """
                    {
                      "exitCode": 0,
                      "stdout": "ok",
                      "stderr": "",
                      "stdoutTruncated": false,
                      "stderrTruncated": false
                    }
                    """));
            }

            if (request.Method == HttpMethod.Get
                && path.EndsWith("/files", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(
                    """
                    {"files":[{"path":"deck.pptx","sizeBytes":4,"sha256":"abcd"}]}
                    """));
            }

            if (request.Method == HttpMethod.Put)
            {
                Assert.EndsWith("/files/scripts/build.py", path, StringComparison.Ordinal);
                return Task.FromResult(JsonResponse(
                    """
                    {"path":"scripts/build.py","sizeBytes":4,"sha256":"abcd"}
                    """));
            }

            if (request.Method == HttpMethod.Get && path.Contains("/files/"))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent("data"u8.ToArray()),
                });
            }

            if (request.Method == HttpMethod.Delete)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            if (request.Method == HttpMethod.Post
                && path.EndsWith("/render", StringComparison.Ordinal))
            {
                return Task.FromResult(JsonResponse(
                    """
                    {
                      "validation":{"passed":true},
                      "slideCount":1,
                      "images":[{
                        "slideNumber":1,
                        "mimeType":"image/png",
                        "data":"aW1hZ2U=",
                        "sizeBytes":5
                      }]
                    }
                    """));
            }

            throw new InvalidOperationException($"Unexpected request: {request.Method} {path}");
        });
        AzurePresentationSessionsClient client = CreateClient(handler);

        PresentationExecResult execution =
            await client.ExecuteAsync("stable-id", "python build.py", 60, default);
        IReadOnlyList<PresentationWorkspaceFile> files =
            await client.ListFilesAsync("stable-id", default);
        PresentationWorkspaceFile written = await client.WriteFileAsync(
            "stable-id",
            "scripts/build.py",
            BinaryData.FromString("data"),
            default);
        BinaryData content = await client.ReadFileAsync(
            "stable-id",
            "deck.pptx",
            default);
        await client.DeleteFileAsync("stable-id", "obsolete.pptx", default);
        PresentationRenderResult render = await client.RenderAsync(
            "stable-id",
            "deck.pptx",
            default);

        Assert.Equal("ok", execution.StandardOutput);
        Assert.Equal("deck.pptx", Assert.Single(files).Path);
        Assert.Equal("scripts/build.py", written.Path);
        Assert.Equal("data", content.ToString());
        Assert.True(render.ValidationPassed);
        Assert.Equal("image", Assert.Single(render.Images).Content.ToString());
    }

    [Theory]
    [InlineData("../secret")]
    [InlineData("a/../secret")]
    [InlineData("/absolute")]
    [InlineData(@"a\secret")]
    public async Task WorkspaceCallsRejectUnsafePaths(string path)
    {
        AzurePresentationSessionsClient client = CreateClient(
            new DelegateHandler(_ => throw new InvalidOperationException("must not send")));

        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.ReadFileAsync("stable-id", path, default));
    }

    [Fact]
    public void BinaryToolResultCarriesRenderedSlide()
    {
        var result = new ToolResultAIContent(new ToolResultObject
        {
            ResultType = "success",
            TextResultForLlm = "Inspect the slide.",
            BinaryResultsForLlm =
            [
                new ToolBinaryResult
                {
                    Type = ToolBinaryResultType.Image,
                    MimeType = "image/png",
                    Data = Convert.ToBase64String("image"u8),
                    Description = "Rendered slide 1",
                },
            ],
        });

        ToolBinaryResult image = Assert.Single(result.Result.BinaryResultsForLlm!);
        Assert.Equal(ToolBinaryResultType.Image, image.Type);
        Assert.Equal("image/png", image.MimeType);
        Assert.Equal("image", Encoding.UTF8.GetString(Convert.FromBase64String(image.Data)));
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
