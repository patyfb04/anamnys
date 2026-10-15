using Anamnys.Server.LiveSession;

namespace Anamnys.Tests.LiveSession;

internal sealed class FakeTranscriber(Func<float[], string> transcribe) : ITranscriber
{
    public int Calls { get; private set; }

    public Task<string> TranscribeAsync(float[] samples, string language, CancellationToken ct)
    {
        Calls++;
        return Task.FromResult(transcribe(samples));
    }
}
