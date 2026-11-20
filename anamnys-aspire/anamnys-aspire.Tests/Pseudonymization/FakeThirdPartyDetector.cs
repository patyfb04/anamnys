using Anamnys.Server.Pseudonymization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Anamnys.Tests.Pseudonymization;

// A detector with fixed answers: the real model is never loaded in tests. Each (text, kind) pair is located in the text
// as the real detector does (every occurrence, on word boundaries).
internal sealed class FakeThirdPartyDetector(Func<string, IReadOnlyList<(string Text, string Kind)>>? answer = null, Exception? fail = null)
    : IThirdPartyDetector
{
    public int Calls { get; private set; }
    public IReadOnlyCollection<string>? LastExclusions { get; private set; }
    public List<int> ChunkLengths { get; } = [];
    // Simulated time of one call (a model run).
    public TimeSpan Delay { get; init; }

    public static FakeThirdPartyDetector Finding(params (string Text, string Kind)[] found) => new(_ => found);

    public async Task<IReadOnlyList<DetectedSpan>> DetectAsync(string text, IReadOnlyCollection<string> exclusions, CancellationToken ct)
    {
        Calls++;
        LastExclusions = exclusions;
        ChunkLengths.Add(text.Length);
        await Task.Delay(Delay, ct);
        ct.ThrowIfCancellationRequested();
        if (fail is not null)
        {
            throw fail;
        }
        return LlamaThirdPartyDetector.Locate(text, answer?.Invoke(text) ?? [], []);
    }
}

internal static class PseudonymizationKit
{
    public static PseudonymizationStep Step(IThirdPartyDetector? detector = null, TimeProvider? clock = null, ILogger<PseudonymizationStep>? logger = null,
        PseudonymizationHolds? holds = null, IDataProtectionProvider? protection = null) =>
        new(Pseudonymizer(detector, clock, protection), holds ?? new PseudonymizationHolds(clock ?? TimeProvider.System), clock ?? TimeProvider.System,
            logger ?? NullLogger<PseudonymizationStep>.Instance);

    public static Pseudonymizer Pseudonymizer(IThirdPartyDetector? detector = null, TimeProvider? clock = null, IDataProtectionProvider? protection = null,
        int chunkChars = 6000, int overlapChars = 200) =>
        new(detector ?? new FakeThirdPartyDetector(), new PatientSecrets(protection ?? new EphemeralDataProtectionProvider()), clock ?? TimeProvider.System)
        {
            ChunkChars = chunkChars,
            OverlapChars = overlapChars,
        };

    // The step's services for a worker scope.
    public static IServiceCollection AddPseudonymizationStep(this IServiceCollection services, PseudonymizationStep step) =>
        services.AddSingleton(step);
}
