using Anamnys.Server.LiveSession;
using FluentAssertions;

namespace Anamnys.Tests.LiveSession;

public class WhisperTranscriberTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Verify_MatchingHash_IsTrueAndMismatch_IsFalse()
    {
        var path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.bin");
        await File.WriteAllTextAsync(path, "abc", Ct);
        try
        {
            const string abcSha256 = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
            (await WhisperModelProvider.VerifyAsync(path, abcSha256, Ct)).Should().BeTrue();
            (await WhisperModelProvider.VerifyAsync(path, abcSha256.ToUpperInvariant(), Ct)).Should().BeTrue();
            (await WhisperModelProvider.VerifyAsync(path, new string('0', 64), Ct)).Should().BeFalse();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CleanSegments_OnlyBlankAudioMarker_IsEmpty() =>
        WhisperTranscriber.CleanSegments([" [BLANK_AUDIO]"]).Should().BeEmpty();

    [Fact]
    public void CleanSegments_NoiseMarkerSegment_IsDropped() =>
        WhisperTranscriber.CleanSegments([" Bom dia.", " (música) ", " Como está?"]).Should().Be("Bom dia. Como está?");

    [Fact]
    public void CleanSegments_DictatedParentheses_AreKeptVerbatim() =>
        WhisperTranscriber.CleanSegments([" Sertralina (50 mg) à noite."]).Should().Be("Sertralina (50 mg) à noite.");

    [Fact]
    public void CleanSegments_MixedSegments_AreJoinedInOrder() =>
        WhisperTranscriber.CleanSegments([" [BLANK_AUDIO]", " Paciente relata insônia", " [risos]", " há duas semanas [sic]."])
            .Should().Be("Paciente relata insônia há duas semanas [sic].");

    // Runs only when a model is already on disk; set ANAMNYS_WHISPER_MODEL to its path.
    // Silence must produce no speech (Whisper may emit "[BLANK_AUDIO]"-style tokens; the
    // transcriber strips bracketed non-speech markers).
    [Fact]
    public async Task Transcribe_TwoSecondsOfSilence_ReturnsEmpty()
    {
        var model = Environment.GetEnvironmentVariable("ANAMNYS_WHISPER_MODEL");
        Assert.SkipWhen(model is null, "ANAMNYS_WHISPER_MODEL is not set.");
        using var factory = Whisper.net.WhisperFactory.FromPath(model!);
        var transcriber = new WhisperTranscriber(_ => Task.FromResult(factory));

        var text = await transcriber.TranscribeAsync(new float[32000], "pt", Ct);

        text.Trim().Should().BeEmpty();
    }
}
