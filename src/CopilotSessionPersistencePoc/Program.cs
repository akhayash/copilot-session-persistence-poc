using Azure.Identity;
using CopilotSessionPersistencePoc.Api;
using CopilotSessionPersistencePoc.AppState;
using CopilotSessionPersistencePoc.ArtifactStorage;
using CopilotSessionPersistencePoc.Copilot;
using CopilotSessionPersistencePoc.Diagnostics;
using CopilotSessionPersistencePoc.Persistence;
using CopilotSessionPersistencePoc.SessionFs;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<SessionOwnerExceptionHandler>();
builder.Services.AddExceptionHandler<SqliteBusyExceptionHandler>();
builder.Services.AddHttpContextAccessor();
builder.Services
    .AddOptions<SessionOwnershipOptions>()
    .Bind(builder.Configuration.GetSection(SessionOwnershipOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services.AddScoped<ISessionOwnerContext, HttpSessionOwnerContext>();
builder.Services
    .AddOptions<PersistenceOptions>()
    .Bind(builder.Configuration.GetSection(PersistenceOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
string persistenceBackend =
    builder.Configuration[$"{PersistenceOptions.SectionName}:Backend"] ?? "Sqlite";
bool useAzureStorage = persistenceBackend.Equals("AzureStorage", StringComparison.OrdinalIgnoreCase);
if (!useAzureStorage
    && !persistenceBackend.Equals("Sqlite", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException(
        "Persistence:Backend must be either 'Sqlite' or 'AzureStorage'.");
}

if (useAzureStorage)
{
    builder.Services
        .AddOptions<AzureStorageOptions>()
        .Bind(builder.Configuration.GetSection(AzureStorageOptions.SectionName))
        .ValidateDataAnnotations()
        .Validate(
            static options => !string.IsNullOrWhiteSpace(options.ConnectionString)
                || options.BlobServiceUri is { IsAbsoluteUri: true }
                    && options.TableServiceUri is { IsAbsoluteUri: true },
            "AzureStorage requires ConnectionString or absolute BlobServiceUri and TableServiceUri.")
        .ValidateOnStart();
}
builder.Services
    .AddOptions<CopilotOptions>()
    .Bind(builder.Configuration.GetSection(CopilotOptions.SectionName))
    .Validate(static options => options.CliUrl.IsAbsoluteUri, "Copilot:CliUrl must be an absolute URI.")
    .Validate(static options => !string.IsNullOrWhiteSpace(options.DefaultModel), "Copilot:DefaultModel is required.")
    .ValidateOnStart();
builder.Services
    .AddOptions<DiagnosticsOptions>()
    .Bind(builder.Configuration.GetSection(DiagnosticsOptions.SectionName))
    .Validate(
        static options => options.MaximumPreviewCharacters is >= 1 and <= 1_000_000,
        "Diagnostics:MaximumPreviewCharacters must be between 1 and 1000000.")
    .ValidateOnStart();
if (useAzureStorage)
{
    builder.Services.AddSingleton<Azure.Core.TokenCredential>(
        static _ => new DefaultAzureCredential());
    builder.Services.AddSingleton<AzureStorageClients>();
    builder.Services.AddSingleton<AzureStorageInitializer>();
    builder.Services.AddSingleton<AzureBlobSessionFsStore>();
    builder.Services.AddScoped<IAppSessionRepository, AzureTableAppSessionRepository>();
    builder.Services.AddSingleton<ISessionFsProviderFactory, AzureBlobSessionFsProviderFactory>();
    builder.Services.AddScoped<ISessionFsDiagnosticsReader, AzureBlobSessionFsDiagnosticsReader>();
    builder.Services.AddSingleton<ISessionLockProvider, AzureBlobSessionLockProvider>();
    builder.Services.AddSingleton<IArtifactStore, AzureBlobArtifactStore>();
}
else
{
    builder.Services.AddSingleton<ISqliteConnectionFactory, SqliteConnectionFactory>();
    builder.Services.AddSingleton<DatabaseInitializer>();
    builder.Services.AddScoped<IAppSessionRepository, SqliteAppSessionRepository>();
    builder.Services.AddSingleton<ISessionFsProviderFactory, SqliteSessionFsProviderFactory>();
    builder.Services.AddScoped<ISessionFsDiagnosticsReader, SqliteSessionFsDiagnosticsReader>();
    builder.Services.AddSingleton<ISessionLockProvider, SessionLockProvider>();
}

builder.Services.AddSingleton<ICopilotClientFactory, CopilotClientFactory>();
builder.Services.AddScoped<CopilotSessionService>();

WebApplication app = builder.Build();

app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapSessionEndpoints();
app.MapFallbackToFile("index.html");

if (useAzureStorage)
{
    await app.Services.GetRequiredService<AzureStorageInitializer>().InitializeAsync();
}
else
{
    await app.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
}

await app.RunAsync();

public partial class Program;
