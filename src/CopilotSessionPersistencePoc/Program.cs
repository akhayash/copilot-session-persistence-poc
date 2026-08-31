using Azure.Identity;
using CopilotSessionPersistencePoc.Api;
using CopilotSessionPersistencePoc.AppState;
using CopilotSessionPersistencePoc.ArtifactStorage;
using CopilotSessionPersistencePoc.Copilot;
using CopilotSessionPersistencePoc.Diagnostics;
using CopilotSessionPersistencePoc.Execution;
using CopilotSessionPersistencePoc.Persistence;
using CopilotSessionPersistencePoc.SessionFs;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;

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
builder.Services
    .AddOptions<DynamicSessionsOptions>()
    .Bind(builder.Configuration.GetSection(DynamicSessionsOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(
        static options => !options.Enabled
            || options.PoolManagementEndpoint is { IsAbsoluteUri: true },
        "DynamicSessions:PoolManagementEndpoint must be absolute when Dynamic Sessions is enabled.")
    .Validate(
        static options => options.StaleJobTimeout
            > TimeSpan.FromSeconds(options.ExecutionTimeoutSeconds + 30),
        "DynamicSessions:StaleJobTimeout must exceed ExecutionTimeoutSeconds by more than 30 seconds.")
    .ValidateOnStart();
builder.Services
    .AddOptions<PresentationSessionsOptions>()
    .Bind(builder.Configuration.GetSection(PresentationSessionsOptions.SectionName))
    .ValidateDataAnnotations()
    .Validate(
        static options => !options.Enabled
            || options.PoolManagementEndpoint is { IsAbsoluteUri: true },
        "PresentationSessions:PoolManagementEndpoint must be absolute when enabled.")
    .ValidateOnStart();
bool enableDynamicSessions = builder.Configuration.GetValue<bool>(
    $"{DynamicSessionsOptions.SectionName}:Enabled");
bool enablePresentationSessions = builder.Configuration.GetValue<bool>(
    $"{PresentationSessionsOptions.SectionName}:Enabled");
bool enableLegacyPresentationTool = builder.Configuration.GetValue<bool>(
    $"{PresentationSessionsOptions.SectionName}:EnableLegacyCreateTool");
if (enableDynamicSessions && !useAzureStorage)
{
    throw new InvalidOperationException(
        "Dynamic Sessions requires Persistence:Backend=AzureStorage.");
}
if (enablePresentationSessions && !useAzureStorage)
{
    throw new InvalidOperationException(
        "Presentation Sessions requires Persistence:Backend=AzureStorage.");
}

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
    builder.Services.AddScoped<IArtifactStore, AzureBlobArtifactStore>();
    builder.Services.AddScoped<IExecutionJobRepository, AzureTableExecutionJobRepository>();
    if (enableDynamicSessions)
    {
        builder.Services.AddHttpClient<IDynamicSessionsClient, AzureDynamicSessionsClient>();
        builder.Services.AddScoped<PythonExecutionCoordinator>();
        builder.Services.AddScoped<ICopilotToolProvider, PythonExecutionToolProvider>();
    }
    if (enablePresentationSessions)
    {
        builder.Services.AddHttpClient<
            IPresentationSessionsClient,
            AzurePresentationSessionsClient>();
        if (enableLegacyPresentationTool)
        {
            builder.Services.AddScoped<PresentationExecutionCoordinator>();
            builder.Services.AddScoped<ICopilotToolProvider, PresentationToolProvider>();
        }
        builder.Services.AddScoped<PresentationWorkspaceCoordinator>();
        builder.Services.AddScoped<ICopilotToolProvider, PresentationWorkspaceToolProvider>();
    }
}
else
{
    builder.Services.AddSingleton<ISqliteConnectionFactory, SqliteConnectionFactory>();
    builder.Services.AddSingleton<DatabaseInitializer>();
    builder.Services.AddScoped<IAppSessionRepository, SqliteAppSessionRepository>();
    builder.Services.AddSingleton<ISessionFsProviderFactory, SqliteSessionFsProviderFactory>();
    builder.Services.AddScoped<ISessionFsDiagnosticsReader, SqliteSessionFsDiagnosticsReader>();
    builder.Services.AddSingleton<ISessionLockProvider, SessionLockProvider>();
    builder.Services.AddSingleton<IArtifactStore, UnavailableArtifactStore>();
}

builder.Services.AddSingleton<ICopilotClientFactory, CopilotClientFactory>();
builder.Services.AddScoped<CopilotSessionService>();

WebApplication app = builder.Build();

app.UseExceptionHandler();

// The SPA shell must always revalidate so a redeployed client is picked up instead of a
// cached older build, while Vite's content-hashed bundles can be cached indefinitely.
StaticFileOptions staticFileOptions = new()
{
    OnPrepareResponse = static context =>
    {
        bool isHashedAsset = context.Context.Request.Path
            .StartsWithSegments("/assets", StringComparison.OrdinalIgnoreCase);
        context.Context.Response.GetTypedHeaders().CacheControl = isHashedAsset
            ? new CacheControlHeaderValue
            {
                Public = true,
                MaxAge = TimeSpan.FromDays(365),
                Extensions = { new NameValueHeaderValue("immutable") },
            }
            : new CacheControlHeaderValue { NoCache = true, MustRevalidate = true };
    },
};

app.UseDefaultFiles();
app.UseStaticFiles(staticFileOptions);
app.MapSessionEndpoints();
app.MapFallbackToFile("index.html", staticFileOptions);

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
