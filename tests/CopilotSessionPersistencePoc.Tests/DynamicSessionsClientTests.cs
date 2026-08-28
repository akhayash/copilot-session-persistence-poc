using System.Net;
using System.Text;
using Azure.Core;
using CopilotSessionPersistencePoc.Execution;
using Microsoft.Extensions.Options;

namespace CopilotSessionPersistencePoc.Tests;

public sealed class DynamicSessionsClientTests
{
    [Fact]
    public async Task ExecuteUsesEntraTokenAndParsesResult()
    {
        HttpRequestMessage? captured = null;
        var handler = new DelegateHandler(async request =>
        {
            captured = request;
            string requestBody = await request.Content!.ReadAsStringAsync();
            Assert.Contains("\"code\":\"print(42)\"", requestBody, StringComparison.Ordinal);
            Assert.Contains("\"timeoutInSeconds\":180", requestBody, StringComparison.Ordinal);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    {
                      "status": "Succeeded",
                      "result": {
                        "stdout": "42\n",
                        "stderr": "",
                        "executionResult": "",
                        "executionTimeInMilliseconds": 7
                      }
                    }
                    """,
                    Encoding.UTF8,
                    "application/json"),
            };
        });
        var client = CreateClient(handler);

        DynamicSessionExecutionResult result = await client.ExecuteCodeAsync(
            "server-only-identifier",
            "print(42)",
            default);

        Assert.Equal("Succeeded", result.Status);
        Assert.Equal("42\n", result.StandardOutput);
        Assert.Equal(7, result.ExecutionTimeInMilliseconds);
        Assert.NotNull(captured);
        Assert.Equal("Bearer", captured.Headers.Authorization!.Scheme);
        Assert.Equal("test-token", captured.Headers.Authorization.Parameter);
        Assert.Contains(
            "identifier=server-only-identifier",
            captured.RequestUri!.Query,
            StringComparison.Ordinal);
        Assert.Contains(
            "api-version=2025-10-02-preview",
            captured.RequestUri.Query,
            StringComparison.Ordinal);
        Assert.EndsWith(
            "/sessionPools/pool/executions",
            captured.RequestUri.AbsolutePath,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedExecutionWithoutResultPreservesServiceStatus()
    {
        var handler = new DelegateHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"status":"TimedOut"}""",
                    Encoding.UTF8,
                    "application/json"),
            }));
        var client = CreateClient(handler);

        DynamicSessionExecutionResult result = await client.ExecuteCodeAsync(
            "server-only-identifier",
            "while True: pass",
            default);

        Assert.Equal("TimedOut", result.Status);
        Assert.Empty(result.StandardOutput);
        Assert.Equal(0, result.ExecutionTimeInMilliseconds);
    }

    [Fact]
    public async Task FileOperationsUseOnlyTheServerIdentifier()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new DelegateHandler(request =>
        {
            requests.Add(request);
            HttpResponseMessage response = request.Method == HttpMethod.Get
                && request.RequestUri!.AbsolutePath.EndsWith(
                    "/files",
                    StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "value": [
                            {
                              "name": "result.csv",
                              "directory": "",
                              "type": "File",
                              "contentType": "text/csv",
                              "sizeInBytes": 12,
                              "lastModifiedAt": "2026-01-01T00:00:00Z"
                            }
                          ]
                        }
                        """,
                        Encoding.UTF8,
                        "application/json"),
                }
                : request.Method == HttpMethod.Get
                    ? new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new ByteArrayContent("x,y\n1,2\n"u8.ToArray()),
                    }
                    : new HttpResponseMessage(
                        request.Method == HttpMethod.Delete
                            ? HttpStatusCode.NoContent
                            : HttpStatusCode.OK);
            return Task.FromResult(response);
        });
        var client = CreateClient(handler);

        await client.UploadFileAsync(
            "secret-id",
            "input.csv",
            "text/csv",
            BinaryData.FromString("x\n1\n"),
            default);
        IReadOnlyList<DynamicSessionFile> files =
            await client.ListFilesAsync("secret-id", default);
        BinaryData downloaded =
            await client.DownloadFileAsync("secret-id", "result.csv", default);
        await client.DeleteSessionAsync("secret-id", default);

        Assert.Single(files);
        Assert.Equal("result.csv", files[0].FileName);
        Assert.Equal("x,y\n1,2\n", downloaded.ToString());
        Assert.Equal(4, requests.Count);
        Assert.All(
            requests,
            request => Assert.Contains(
                "identifier=secret-id",
                request.RequestUri!.Query,
                StringComparison.Ordinal));
    }

    [Fact]
    public async Task ListFilesFollowsSameOriginContinuationLinks()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new DelegateHandler(request =>
        {
            requests.Add(request);
            bool secondPage = request.RequestUri!.Query.Contains(
                "page=2",
                StringComparison.Ordinal);
            string body = secondPage
                ? """
                  {
                    "value": [
                      {
                        "name": "second.csv",
                        "directory": "",
                        "type": "File",
                        "sizeInBytes": 2
                      }
                    ]
                  }
                  """
                : """
                  {
                    "value": [
                      {
                        "name": "first.csv",
                        "directory": "",
                        "type": "File",
                        "sizeInBytes": 1
                      }
                    ],
                    "nextLink": "https://japaneast.dynamicsessions.io/subscriptions/sub/resourceGroups/rg/sessionPools/pool/files?api-version=2025-10-02-preview&identifier=secret-id&page=2"
                  }
                  """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        });
        var client = CreateClient(handler);

        IReadOnlyList<DynamicSessionFile> files =
            await client.ListFilesAsync("secret-id", default);

        Assert.Equal(["first.csv", "second.csv"], files.Select(file => file.FileName));
        Assert.Equal(2, requests.Count);
        Assert.All(
            requests,
            request => Assert.Equal(
                "Bearer",
                request.Headers.Authorization?.Scheme));
    }

    private static AzureDynamicSessionsClient CreateClient(
        HttpMessageHandler handler) =>
        new(
            new HttpClient(handler),
            new TestTokenCredential(),
            Options.Create(new DynamicSessionsOptions
            {
                Enabled = true,
                PoolManagementEndpoint = new Uri(
                    "https://japaneast.dynamicsessions.io/subscriptions/sub/"
                    + "resourceGroups/rg/sessionPools/pool"),
            }));

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
