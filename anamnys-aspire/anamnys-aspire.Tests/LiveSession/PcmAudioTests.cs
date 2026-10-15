using Anamnys.Server.LiveSession;
using FluentAssertions;

namespace Anamnys.Tests.LiveSession;

public class PcmAudioTests
{
    [Fact]
    public void ToSamples_LittleEndianInt16_ScalesToMinusOneToOne()
    {
        // 0, 16384, -32768, 32767
        var audio = new PcmAudio([0x00, 0x00, 0x00, 0x40, 0x00, 0x80, 0xFF, 0x7F, 0xAA], length: 8);

        var samples = audio.ToSamples();

        samples.Should().Equal([0f, 0.5f, -1f, 32767f / 32768f]);
    }

    [Fact]
    public void Destroy_ZeroesTheBufferAndEmptiesIt()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var audio = new PcmAudio(bytes, 4);

        audio.Destroy();

        bytes.Should().OnlyContain(b => b == 0);
        audio.Length.Should().Be(0);
        audio.ToSamples().Should().BeEmpty();
    }
}
