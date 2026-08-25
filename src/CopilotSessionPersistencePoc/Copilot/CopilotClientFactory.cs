using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Microsoft.Extensions.Options;

namespace CopilotSessionPersistencePoc.Copilot;

public sealed class CopilotClientFactory(
    IOptions<CopilotOptions> options,
    ILoggerFactory loggerFactory) : ICopilotClientFactory, IAsyncDisposable
{
    private readonly SemaphoreSlim _startLock = new(1, 1);
    private CopilotClient? _client;

    public async Task<CopilotClient> GetClientAsync(CancellationToken cancellationToken = default)
    {
        if (_client is not null)
        {
            return _client;
        }

        await _startLock.WaitAsync(cancellationToken);
        try
        {
            if (_client is not null)
            {
                return _client;
            }

            CopilotOptions settings = options.Value;
            string? connectionToken = Environment.GetEnvironmentVariable("COPILOT_CONNECTION_TOKEN");
            var client = new CopilotClient(new CopilotClientOptions
            {
                Connection = RuntimeConnection.ForUri(settings.CliUrl.AbsoluteUri, connectionToken),
                Mode = CopilotClientMode.Empty,
                SessionFs = new SessionFsConfig
                {
                    InitialWorkingDirectory = "/",
                    SessionStatePath = "/session-state",
                    Conventions = SessionFsSetProviderConventions.Posix,
                },
                Logger = loggerFactory.CreateLogger<CopilotClient>(),
            });

            try
            {
                await client.StartAsync(cancellationToken);
            }
            catch
            {
                await client.DisposeAsync();
                throw;
            }

            _client = client;
            return client;
        }
        finally
        {
            _startLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }

        _startLock.Dispose();
    }
}
