using CopilotSessionPersistencePoc.Api;
using CopilotSessionPersistencePoc.AppState;
using CopilotSessionPersistencePoc.Copilot;
using CopilotSessionPersistencePoc.Diagnostics;
using CopilotSessionPersistencePoc.Persistence;
using CopilotSessionPersistencePoc.SessionFs;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<SqliteBusyExceptionHandler>();
builder.Services
    .AddOptions<PersistenceOptions>()
    .Bind(builder.Configuration.GetSection(PersistenceOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
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

builder.Services.AddSingleton<ISqliteConnectionFactory, SqliteConnectionFactory>();
builder.Services.AddSingleton<DatabaseInitializer>();
builder.Services.AddScoped<IAppSessionRepository, SqliteAppSessionRepository>();
builder.Services.AddSingleton<ISessionFsProviderFactory, SqliteSessionFsProviderFactory>();
builder.Services.AddScoped<ISessionFsDiagnosticsReader, SqliteSessionFsDiagnosticsReader>();
builder.Services.AddSingleton<ICopilotClientFactory, CopilotClientFactory>();
builder.Services.AddSingleton<SessionLockProvider>();
builder.Services.AddScoped<CopilotSessionService>();

WebApplication app = builder.Build();

app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapSessionEndpoints();
app.MapFallbackToFile("index.html");

await app.Services.GetRequiredService<DatabaseInitializer>().InitializeAsync();
await app.RunAsync();

public partial class Program;
