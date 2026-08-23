using Anamnys.Api.Hubs;
using QuestPDF.Infrastructure;
using Anamnys.Api.Jobs;
using Anamnys.Application.Interfaces;
using Anamnys.Infrastructure.Data;
using Anamnys.Infrastructure.Jobs;
using Anamnys.Infrastructure.Services;
using Hangfire;
using Hangfire.PostgreSql;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using System.Text;

// QuestPDF community license (free for open-source / internal tools)
QuestPDF.Settings.License = LicenseType.Community;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// ─── Database ──────────────────────────────────────────────────────────────────
builder.Services.AddDbContext<AppDbContext>(opts =>
{
    opts.UseNpgsql(cfg.GetConnectionString("Default"));

    // Suppress snapshot-drift warning in dev — snapshot was written manually
    // (dotnet ef tools unavailable in this environment).
    // Run `dotnet ef migrations add` to regenerate a clean snapshot before production.
    if (builder.Environment.IsDevelopment())
        opts.ConfigureWarnings(w =>
            w.Log(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
});

// ─── AI Services ───────────────────────────────────────────────────────────────
builder.Services.Configure<WhisperOptions>(cfg.GetSection("Whisper"));
builder.Services.AddSingleton<WhisperTranscriptionService>();
builder.Services.AddSingleton<ITranscriptionService>(sp =>
    sp.GetRequiredService<WhisperTranscriptionService>());

builder.Services.Configure<LlamaOptions>(cfg.GetSection("Llama"));
builder.Services.AddSingleton<LlamaStructuringService>();
builder.Services.AddSingleton<INoteStructuringService>(sp =>
    sp.GetRequiredService<LlamaStructuringService>());

// BillingEngineRouter dispatches to the correct country engine (US, Canada, Brazil, Europe)
builder.Services.AddScoped<IBillingEngine, Anamnys.Infrastructure.Services.Billing.BillingEngineRouter>();

// ─── Two-factor auth (TOTP) ──────────────────────────────────────────────────────
// AddDataProtection() registers IDataProtectionProvider, used by TwoFactorService to encrypt
// TOTP secrets at rest. Keys default to the local filesystem in dev; for a multi-instance
// production deployment, persist them centrally (e.g. .PersistKeysToStackExchangeRedis /
// .PersistKeysToAzureBlobStorage) or every instance restart invalidates enrolled secrets.
builder.Services.AddDataProtection();
builder.Services.AddScoped<ITwoFactorService, TwoFactorService>();

// ─── Storage ───────────────────────────────────────────────────────────────────
builder.Services.Configure<S3StorageOptions>(cfg.GetSection("Storage"));
builder.Services.AddScoped<IStorageService, S3StorageService>();

// ─── Email (Resend) ────────────────────────────────────────────────────────────
// Resend:ApiKey is a secret — set via `dotnet user-secrets set "Resend:ApiKey" "re_..."` in
// dev, never committed to appsettings.*.json. Typed HttpClient so ResendEmailService gets a
// pooled/managed HttpClient instead of constructing its own.
builder.Services.Configure<ResendOptions>(cfg.GetSection("Resend"));
builder.Services.AddHttpClient<IEmailService, ResendEmailService>();
builder.Services.Configure<Anamnys.Api.Controllers.ContactOptions>(cfg.GetSection("Contact"));

// ─── Background Jobs (Hangfire) ────────────────────────────────────────────────
builder.Services.AddHangfire(hf =>
    hf.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
      .UseSimpleAssemblyNameTypeSerializer()
      .UseRecommendedSerializerSettings()
      .UsePostgreSqlStorage(cfg.GetConnectionString("Default")));
builder.Services.AddHangfireServer(opts =>
{
    opts.WorkerCount = 2; // limit concurrency — LLM is memory-intensive
});
builder.Services.AddScoped<PipelineJob>();
builder.Services.AddScoped<DatabaseMaintenanceJob>();

// ─── MediatR ───────────────────────────────────────────────────────────────────
builder.Services.AddMediatR(cfg =>
    cfg.RegisterServicesFromAssembly(
        typeof(Anamnys.Application.Commands.TranscribeAudio.TranscribeAudioCommand).Assembly));

// ─── SignalR ───────────────────────────────────────────────────────────────────
builder.Services.AddSignalR();

// ─── Auth (JWT) ────────────────────────────────────────────────────────────────
var jwtKey = cfg["Jwt:Key"] ?? throw new InvalidOperationException("Jwt:Key not configured");
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opts =>
    {
        opts.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ValidateIssuer = false,
            ValidateAudience = false,
        };

        opts.Events = new JwtBearerEvents
        {
            // Token resolution order: SignalR query string (native + web hubs) -> Authorization
            // header (native REST calls, untouched default behavior) -> the HttpOnly cookie
            // (web REST calls, which no longer send an Authorization header at all — see
            // services/api.ts on the client).
            OnMessageReceived = ctx =>
            {
                var accessToken = ctx.Request.Query["access_token"];
                var path = ctx.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                {
                    ctx.Token = accessToken;
                    return Task.CompletedTask;
                }

                if (!ctx.Request.Headers.ContainsKey("Authorization")
                    && ctx.Request.Cookies.TryGetValue("auth_token", out var cookieToken))
                {
                    ctx.Token = cookieToken;
                }

                return Task.CompletedTask;
            },

            // The short-lived "2fa-challenge" token AuthController issues between the password
            // and code steps of login is deliberately never meant to authorize normal API
            // calls (see AuthController.GenerateChallengeToken) — reject it everywhere except
            // the one endpoint designed to consume it, in case a leaked/logged challenge token
            // gets replayed as a Bearer header or cookie.
            OnTokenValidated = ctx =>
            {
                var purpose = ctx.Principal?.FindFirst("purpose")?.Value;
                if (purpose == "2fa-challenge" && ctx.HttpContext.Request.Path != "/api/auth/login/2fa")
                    ctx.Fail("Challenge token cannot be used for API access.");
                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddAuthorization();

// ─── API ───────────────────────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "Anamnys AI API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        In = ParameterLocation.Header,
        Description = "Enter: Bearer {token}",
        Name = "Authorization",
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } },
            Array.Empty<string>()
        }
    });
});

// AllowAnyOrigin is incompatible with credentialed requests (browsers reject the combination
// outright) — now that the web client sends its auth cookie cross-origin, CORS must name
// specific origins and opt into AllowCredentials(). Configure real origin(s) via
// Cors:AllowedOrigins before deploying anywhere beyond local dev (nothing is committed here).
var allowedOrigins = cfg.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? new[] { "http://localhost:8081" };
builder.Services.AddCors(opts =>
    opts.AddDefaultPolicy(p => p
        .WithOrigins(allowedOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()
        .AllowCredentials()));

var app = builder.Build();

// ─── Middleware ────────────────────────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

// ─── SignalR hubs ──────────────────────────────────────────────────────────────
app.MapHub<ProgressHub>("/hubs/progress");
app.MapHub<TranscriptionHub>("/hubs/transcription");

// ─── Hangfire dashboard (dev only) ────────────────────────────────────────────
if (app.Environment.IsDevelopment())
    app.UseHangfireDashboard("/jobs");

// ─── Auto-migrate + seed on startup ───────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var startupLogger = scope.ServiceProvider.GetRequiredService<ILogger<AppDbContext>>();

    await db.Database.MigrateAsync();

    if (app.Environment.IsDevelopment())
        await Anamnys.Infrastructure.Data.DevDataSeeder.SeedAsync(db, startupLogger);
}

// ─── Recurring database maintenance ───────────────────────────────────────────
// The four routines in src/backend/db/03_jobs.sql. Each exists because a
// constraint could not express the rule; Hangfire is only the clock.
// Registration checks the routines exist first and refuses to schedule jobs that
// would fail hourly inside a background worker where nobody is watching.
await app.Services.AddDatabaseMaintenanceJobsAsync();

await app.RunAsync();
