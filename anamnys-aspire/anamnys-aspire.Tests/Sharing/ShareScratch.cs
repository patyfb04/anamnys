using System.Text;
using Anamnys.Server.Data;
using Anamnys.Server.ExternalDocuments;
using Anamnys.Server.Sharing;
using Anamnys.Tests.Notes;
using Anamnys.Tests.Retention;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Anamnys.Tests.Sharing;

// A scratch database from the bootstrap script, which carries the F-17/F-18 columns, so these tests do not depend on the
// dev database having had design/migrations/2026-11-01-protected-sharing.sql applied.
internal sealed class ShareScratch(RetentionScratch db, Guid provider, Guid patient, Guid document) : IAsyncDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 11, 1, 15, 0, 0, TimeSpan.Zero);

    public RetentionScratch Db { get; } = db;
    public Guid Provider { get; } = provider;
    public Guid Patient { get; } = patient;
    public Guid Document { get; } = document;
    public FakeNoteFileStore Store { get; } = new();
    public IVirusScanner Scanner { get; set; } = new FakeVirusScanner();
    // Room for the concurrency tests: the real limits have their own tests.
    public ShareAttemptLimiter Limiter { get; set; } = new(4, 100);

    public static RecordAuthorizationRequest Request(string recipient = "Dr. Pedro Lima", string purpose = "Continuidade do cuidado") =>
        new(recipient, "Psiquiatra", purpose, Now.AddDays(-1), "signed_term", true);

    public static async Task<ShareScratch> CreateAsync(SharedAppHostFixture fixture, CancellationToken ct)
    {
        var db = await RetentionScratch.CreateAsync(fixture, ct);
        var provider = await db.AddProviderAsync(ct, withCrp: true);
        var patient = await db.AddPatientAsync(provider, "Ana", ct);
        var document = await db.AddSignedDocumentAsync(provider, patient, Now.AddDays(-2), ct);
        return new ShareScratch(db, provider, patient, document);
    }

    public async Task<AuthorizationResult> AuthorizeAsync(CancellationToken ct, RecordAuthorizationRequest? request = null, TermUpload? term = null, Guid? provider = null, Guid? document = null)
    {
        await using var context = Db.CreateDbContext();
        return await ShareService.RecordAuthorizationAsync(
            context, Store, Scanner, provider ?? Provider, document ?? Document, request ?? Request(), term, Now, ct);
    }

    public async Task<LinkResult> LinkAsync(Guid authorizationId, CancellationToken ct, int? days = null, int? cap = null, bool? download = null, DateTimeOffset? at = null)
    {
        await using var context = Db.CreateDbContext();
        return await ShareService.CreateLinkAsync(
            context, Limiter, Provider, Document, new CreateShareLinkRequest(authorizationId, days, cap, download), at ?? Now, ct);
    }

    // An authorization and a link on the scenario's document.
    public async Task<CreatedShareLink> SharedAsync(CancellationToken ct, int? cap = null, bool? download = null, DateTimeOffset? at = null, string recipient = "Dr. Pedro Lima")
    {
        var authorization = await AuthorizeAsync(ct, Request(recipient, $"Finalidade {Guid.NewGuid():N}"));
        return (await LinkAsync(authorization.Id!.Value, ct, cap: cap, download: download, at: at)).Link!;
    }

    public async Task<PublicOpenResult> OpenAsync(string token, string password, CancellationToken ct, DateTimeOffset? at = null)
    {
        await using var context = Db.CreateDbContext();
        return await PublicShareService.OpenAsync(context, Limiter, new FakeTimeProvider(at ?? Now), token, password, "Agente de teste", ct);
    }

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}

// The real public routes over real HTTP, on Kestrel and a scratch database, logging everything at Trace level: the AppHost's
// server reads the dev database, and the rate-limit counters must start at zero.
internal sealed class PublicShareHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    private PublicShareHost(WebApplication app, HttpClient client, CapturedLogs logs)
    {
        _app = app;
        Client = client;
        Logs = logs;
    }

    public HttpClient Client { get; }
    public CapturedLogs Logs { get; }

    public static async Task<PublicShareHost> StartAsync(
        RetentionScratch db, TimeProvider clock, CancellationToken ct, ShareAttemptLimiter? limiter = null, int allClientsPerMinute = ShareEndpoints.AllClientsRequestsPerMinute)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var logs = new CapturedLogs();
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        // Beats the Warning of the appsettings copied next to the tests: the request logging must be seen here.
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Trace);
        builder.Logging.AddProvider(logs);
        builder.Services.AddScoped(_ => db.CreateDbContext());
        builder.Services.AddSingleton(clock);
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy("public", _ => System.Threading.RateLimiting.RateLimitPartition.GetNoLimiter("all"));
            o.AddShareRateLimits(allClientsPerMinute);
        });
        builder.Services.AddSingleton(limiter ?? new ShareAttemptLimiter());
        var app = builder.Build();
        app.UseRateLimiter();
        app.MapPublicShareEndpoints();
        await app.StartAsync(ct);
        return new PublicShareHost(app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) }, logs);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
    }
}

internal sealed class CapturedLogs : ILoggerProvider
{
    private readonly StringBuilder _text = new();

    public string Text
    {
        get
        {
            lock (_text)
            {
                return _text.ToString();
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class Logger(CapturedLogs owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner._text)
            {
                owner._text.Append(category).Append(' ').Append(formatter(state, exception)).Append(' ').AppendLine(exception?.ToString());
            }
        }
    }
}
