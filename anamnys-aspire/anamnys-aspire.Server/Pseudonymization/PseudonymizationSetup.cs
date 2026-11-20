using Anamnys.Server.NoteStructuring;
using Microsoft.Extensions.Options;

namespace Anamnys.Server.Pseudonymization;

public static class PseudonymizationSetup
{
    // The backfill's detector: the same model provider and gate as live work, but it only gets the gate when no interactive
    // request waits.
    internal static LlamaThirdPartyDetector BackfillDetector(
        Func<CancellationToken, Task<(LLama.LLamaWeights Weights, LLama.Common.ModelParams Params)>> load,
        NoteStructuringOptions options, LlmGate gate) =>
        new(load, options, gate, LlmPriority.Background);

    // After AddNoteStructuring: the detector uses its model provider and gate (one model instance per process).
    public static void AddPseudonymization(this WebApplicationBuilder builder)
    {
        // The secrets' own key ring, persisted in Postgres; outside Development the keys are encrypted with the
        // DataProtection:CertificateThumbprint certificate (the same one the auth ring requires there).
        var certificate = PseudonymizationKeyRing.Certificate(builder.Configuration);
        if (certificate is null && !builder.Environment.IsDevelopment())
        {
            throw new InvalidOperationException(
                "DataProtection:CertificateThumbprint is required outside Development: it encrypts the pseudonymisation key ring at rest.");
        }
        // Certificates of earlier rotations decrypt the older keys (DataProtection:PreviousCertificateThumbprints).
        var previous = PseudonymizationKeyRing.PreviousCertificates(builder.Configuration);
        builder.Services.AddSingleton(sp => new PatientSecrets(PseudonymizationKeyRing.Create(
            new PostgresXmlRepository(sp.GetRequiredService<IConfiguration>().GetConnectionString("anamnysdb")
                ?? throw new InvalidOperationException("No connection string for anamnysdb.")),
            certificate, sp.GetRequiredService<ILoggerFactory>(), previous)));
        builder.Services.AddSingleton<IThirdPartyDetector>(sp => new LlamaThirdPartyDetector(
            sp.GetRequiredService<LlmModelProvider>(), sp.GetRequiredService<IOptions<NoteStructuringOptions>>(),
            sp.GetRequiredService<LlmGate>()));
        builder.Services.AddSingleton<Pseudonymizer>();
        builder.Services.AddSingleton<PseudonymizationHolds>();
        builder.Services.AddSingleton<PseudonymizationStep>();
        // The backfill converts legacy transcripts at the lowest priority: a second detector over the same model provider
        // and gate, yielding to every live request.
        builder.Services.AddSingleton(sp => new BackfillWorker(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new Pseudonymizer(
                BackfillDetector(sp.GetRequiredService<LlmModelProvider>().GetAsync,
                    sp.GetRequiredService<IOptions<NoteStructuringOptions>>().Value, sp.GetRequiredService<LlmGate>()),
                sp.GetRequiredService<PatientSecrets>(), sp.GetRequiredService<TimeProvider>()),
            sp.GetRequiredService<TimeProvider>(), sp.GetRequiredService<ILogger<BackfillWorker>>()));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<BackfillWorker>());
    }
}
