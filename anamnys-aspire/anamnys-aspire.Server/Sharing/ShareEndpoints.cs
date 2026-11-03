using System.Globalization;
using System.Threading.RateLimiting;
using Anamnys.Server.Data;
using Anamnys.Server.ExternalDocuments;
using Anamnys.Server.Notifications;
using Anamnys.Server.Patients;
using Anamnys.Server.Storage;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace Anamnys.Server.Sharing;

// design/specs/2026-11-01-protected-sharing-design.md §3. In API calls the token travels only in POST bodies, so it is in
// no API URL. The recipient's page is different: the browser's GET of /s/{token} has the token in its path. IsTokenPagePath
// keeps it out of this server's traces, but a reverse proxy's access log sees it, so those logs must be scrubbed at deployment.
public static class ShareEndpoints
{
    public const string PasswordRateLimitPolicy = "share-password";
    public const int PasswordAttemptsPerMinute = 10;
    public const int AllClientsRequestsPerMinute = 120;

    private const long MaxRequestBytes = ExternalDocumentService.MaxBytes + 1024 * 1024;
    private const long MaxPublicBodyBytes = 4096;
    private const string PublicRoute = "/api/public/shares";
    private const string BusyRetrySeconds = "5";

    // Per client on the password route, and one bucket for all clients on every public share route: many addresses
    // together must not be able to keep the Argon2 slots full.
    public static void AddShareRateLimits(this RateLimiterOptions options, int allClientsPerMinute = AllClientsRequestsPerMinute)
    {
        options.AddPolicy(PasswordRateLimitPolicy, http =>
            RateLimitPartition.GetFixedWindowLimiter(
                ShareNetwork.PartitionKey(http.Connection.RemoteIpAddress),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = PasswordAttemptsPerMinute, Window = TimeSpan.FromMinutes(1) }));
        options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(http =>
            http.Request.Path.StartsWithSegments(PublicRoute)
                ? RateLimitPartition.GetFixedWindowLimiter(PublicRoute, _ => new FixedWindowRateLimiterOptions { PermitLimit = allClientsPerMinute, Window = TimeSpan.FromMinutes(1) })
                : RateLimitPartition.GetNoLimiter("other"));
    }

    public static bool IsTokenPagePath(PathString path) =>
        path.StartsWithSegments("/s") || path.StartsWithSegments("/patient/s");

    public static void MapPublicShareEndpoints(this IEndpointRouteBuilder app)
    {
        var shares = app.MapGroup(PublicRoute)
            .WithMetadata(new RequestSizeLimitAttribute(MaxPublicBodyBytes))
            .AddEndpointFilter(async (context, next) =>
            {
                var headers = context.HttpContext.Response.Headers;
                headers.CacheControl = "no-store";
                headers["X-Robots-Tag"] = "noindex";
                headers["Referrer-Policy"] = "no-referrer";
                headers.XContentTypeOptions = "nosniff";
                return await next(context);
            });

        shares.MapPost("check", async (PublicShareCheckRequest? request, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
            await PublicShareService.CheckAsync(db, request?.Token ?? "", clock.GetUtcNow(), ct)
                ? Results.Ok(new { available = true })
                : Unavailable())
            .RequireRateLimiting(ConfirmationEndpoints.RateLimitPolicy);

        shares.MapPost("open", async (PublicShareOpenRequest? request, HttpContext http, AnamnysDbContext db, ShareAttemptLimiter limiter, TimeProvider clock, CancellationToken ct) =>
        {
            var result = await PublicShareService.OpenAsync(
                db, limiter, clock, request?.Token ?? "", request?.Password ?? "", http.Request.Headers.UserAgent.ToString(), ct);
            switch (result.Outcome)
            {
                case PublicOpenOutcome.Opened:
                    http.Response.Headers.ContentDisposition = result.DownloadEnabled ? $"attachment; filename=\"{result.FileName}\"" : "inline";
                    return Results.Bytes(result.Pdf!, "application/pdf");
                case PublicOpenOutcome.WrongPassword:
                    return Results.Json(new { message = "Senha incorreta.", code = "wrong_password" }, statusCode: StatusCodes.Status403Forbidden);
                case PublicOpenOutcome.Busy:
                    return Busy(http);
                default:
                    return Unavailable();
            }
        }).RequireRateLimiting(PasswordRateLimitPolicy);
    }

    public static void MapShareEndpoints(this RouteGroupBuilder phi)
    {
        var group = phi.MapGroup("providers/me");

        group.MapGet("documents/{documentId:guid}/shares", async (Guid documentId, HttpContext http, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            return await ShareService.ListAsync(db, providerId, documentId, clock.GetUtcNow(), ct) is { } shares
                ? Results.Ok(shares)
                : Results.NotFound();
        });

        group.MapPost("documents/{documentId:guid}/disclosure-authorizations", async (
            Guid documentId, HttpContext http, AnamnysDbContext db, INoteFileStore store, IVirusScanner scanner, TimeProvider clock, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            // Refuse before reading the body when the document cannot be shared.
            var state = await ShareService.StateAsync(db, providerId, documentId, ct);
            if (state != ShareOutcome.Ok)
            {
                return Refusal(state);
            }
            if (!http.Request.HasFormContentType)
            {
                return Results.BadRequest(new { message = "Envie o formulário como multipart/form-data.", code = "invalid_form" });
            }
            var (form, formFailure) = await ReadFormAsync(http, ct);
            if (form is null)
            {
                return formFailure!;
            }

            var now = clock.GetUtcNow();
            var request = new RecordAuthorizationRequest(
                form["recipientName"].ToString(), form["recipientRole"].ToString(), form["purpose"].ToString(),
                DateTimeOffset.TryParse(form["authorizedAt"].ToString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null,
                form["obtainedVia"].ToString(), string.Equals(form["confirmed"].ToString(), "true", StringComparison.OrdinalIgnoreCase));
            var errors = request.Validate(now);
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            TermUpload? term = null;
            if (form.Files.GetFile("term") is { } file)
            {
                if (file.Length > ExternalDocumentService.MaxBytes)
                {
                    return Refusal(ShareOutcome.TermTooLarge);
                }
                var content = new byte[file.Length];
                await using (var input = file.OpenReadStream())
                {
                    await input.ReadExactlyAsync(content, ct);
                }
                term = new TermUpload(file.FileName ?? "", content);
            }

            var result = await ShareService.RecordAuthorizationAsync(db, store, scanner, providerId, documentId, request, term, now, ct);
            return result.Outcome switch
            {
                ShareOutcome.Ok => Results.Json(new { id = result.Id }, statusCode: StatusCodes.Status201Created),
                ShareOutcome.AuthorizationExists => Results.Conflict(new
                {
                    message = "Já existe uma autorização para este destinatário e finalidade.", code = "authorization_exists", id = result.Id,
                }),
                _ => Refusal(result.Outcome),
            };
        });

        group.MapPost("documents/{documentId:guid}/share-links", async (
            Guid documentId, CreateShareLinkRequest request, HttpContext http, AnamnysDbContext db, ShareAttemptLimiter limiter, IConfiguration configuration, TimeProvider clock, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var errors = request.Validate();
            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }
            var result = await ShareService.CreateLinkAsync(db, limiter, providerId, documentId, request, clock.GetUtcNow(), ct);
            if (result.Outcome == ShareOutcome.Busy)
            {
                return Busy(http);
            }
            if (result.Link is not { } link)
            {
                return Refusal(result.Outcome);
            }
            http.Response.Headers.CacheControl = "no-store";
            var portal = PatientInvitationEndpoints.PortalBaseUrl(http, configuration).ToString().TrimEnd('/');
            return Results.Json(new
            {
                id = link.Id,
                url = $"{portal}/s/{link.Token}",
                password = link.Password,
                expiresAt = link.ExpiresAt,
                viewCap = link.ViewCap,
                downloadEnabled = link.DownloadEnabled,
            }, statusCode: StatusCodes.Status201Created);
        });

        // Streamed, not presigned: no link to the term exists outside this response.
        group.MapPost("disclosure-authorizations/{authorizationId:guid}/term/open", async (
            Guid authorizationId, HttpContext http, AnamnysDbContext db, INoteFileStore store, TimeProvider clock, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            var term = await ShareService.OpenTermAsync(db, store, providerId, authorizationId, clock.GetUtcNow(), ct);
            if (term.Outcome != ShareOutcome.Ok)
            {
                return Refusal(term.Outcome);
            }
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers.XContentTypeOptions = "nosniff";
            http.Response.Headers.ContentDisposition = "inline";
            return Results.Bytes(term.Content!, term.ContentType);
        });

        group.MapPost("share-links/{linkId:guid}/revoke", async (Guid linkId, HttpContext http, AnamnysDbContext db, TimeProvider clock, CancellationToken ct) =>
        {
            if (await PatientRecordEndpoints.ProviderIdAsync(http) is not { } providerId)
            {
                return Results.Unauthorized();
            }
            return await ShareService.RevokeAsync(db, providerId, linkId, clock.GetUtcNow(), ct) == ShareOutcome.Ok
                ? Results.NoContent()
                : Results.NotFound();
        });
    }

    // One response for every link that does not open: same status, same body.
    private static IResult Unavailable() =>
        Results.Json(new { message = "Este link não está disponível.", code = "share_unavailable" }, statusCode: StatusCodes.Status404NotFound);

    // No slot for an Argon2 run: the same 429 as the rate limits, with a body that says why and when to try again.
    private static IResult Busy(HttpContext http)
    {
        http.Response.Headers.RetryAfter = BusyRetrySeconds;
        return Results.Json(new { message = "Muitos acessos no momento. Tente de novo em instantes.", code = "busy" }, statusCode: StatusCodes.Status429TooManyRequests);
    }

    // The term is patient data: kept in memory, as for received documents, and the request is capped.
    private static async Task<(IFormCollection? Form, IResult? Failure)> ReadFormAsync(HttpContext http, CancellationToken ct)
    {
        var limit = http.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false })
        {
            limit.MaxRequestBodySize = MaxRequestBytes;
        }
        http.Features.Set<IFormFeature>(new FormFeature(http.Request, new FormOptions
        {
            MemoryBufferThreshold = (int)MaxRequestBytes,
            MultipartBodyLengthLimit = MaxRequestBytes,
        }));
        try
        {
            return (await http.Request.ReadFormAsync(ct), null);
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return (null, Refusal(ShareOutcome.TermTooLarge));
        }
        catch (InvalidDataException e) when (e.Message.Contains("length limit", StringComparison.OrdinalIgnoreCase))
        {
            return (null, Refusal(ShareOutcome.TermTooLarge));
        }
        catch (InvalidDataException)
        {
            return (null, Results.BadRequest(new { message = "Formulário inválido.", code = "invalid_form" }));
        }
    }

    private static IResult Refusal(ShareOutcome outcome) => outcome switch
    {
        ShareOutcome.NotSigned => Results.Conflict(new { message = "Assine o documento antes de compartilhar.", code = "not_signed" }),
        ShareOutcome.Superseded => Results.Conflict(new { message = "Este documento foi corrigido: compartilhe a versão mais recente.", code = "document_superseded" }),
        ShareOutcome.Archived => Results.Conflict(new { message = "Paciente arquivado: não é possível criar novos compartilhamentos.", code = "archived" }),
        ShareOutcome.AuthorizationRequired => Results.Conflict(new { message = "Registre a autorização do paciente antes de compartilhar.", code = "authorization_required" }),
        ShareOutcome.TermEmpty => Results.BadRequest(new { message = "O arquivo está vazio.", code = "empty" }),
        ShareOutcome.TermTooLarge => Results.BadRequest(new { message = "O arquivo excede 20 MB.", code = "too_large" }),
        ShareOutcome.TermTypeMismatch => Results.BadRequest(new { message = "A extensão do arquivo não corresponde ao seu conteúdo.", code = "type_mismatch" }),
        ShareOutcome.TermUnsupportedType => Results.BadRequest(new { message = "Envie um arquivo PDF, JPG ou PNG.", code = "unsupported_type" }),
        ShareOutcome.TermInfected => Results.BadRequest(new { message = "O arquivo foi recusado pela verificação de segurança.", code = "infected" }),
        ShareOutcome.ScannerUnavailable => Results.Json(new { message = "Verificação de arquivos indisponível. Tente de novo em instantes.", code = "scanner_unavailable" },
            statusCode: StatusCodes.Status503ServiceUnavailable),
        ShareOutcome.StorageUnavailable => Results.Json(new { message = "Armazenamento indisponível. Tente de novo em instantes.", code = "storage_unavailable" },
            statusCode: StatusCodes.Status503ServiceUnavailable),
        _ => Results.NotFound(),
    };
}
