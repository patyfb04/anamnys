using Anamnys.Server.LiveSession;
using FluentAssertions;
using Microsoft.Extensions.Time.Testing;

namespace Anamnys.Tests.LiveSession;

public class RecordingBuffersTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 10, 13, 0, 0, TimeSpan.Zero);
    private static readonly Guid Provider = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    private static RecordingMeta Meta(Guid provider) => new(provider, Guid.NewGuid(), null, "DAP", "pt", Start);

    [Fact]
    public void Append_InOrder_AcceptsAndTake_ReturnsAllBytes()
    {
        var buffers = new RecordingBuffers(new FakeTimeProvider(Start));
        var id = Guid.NewGuid();
        buffers.Begin(id, Meta(Provider)).Should().Be(BeginStatus.Opened);

        buffers.Append(id, Provider, 0, [1, 2]).Should().Be(new ChunkResult(ChunkStatus.Accepted, 0));
        buffers.Append(id, Provider, 1, [3, 4]).Should().Be(new ChunkResult(ChunkStatus.Accepted, 1));
        var taken = buffers.Take(id, Provider);

        taken.Should().NotBeNull();
        taken!.Audio.Length.Should().Be(4);
        taken.Audio.ToSamples().Should().HaveCount(2);
        buffers.Take(id, Provider).Should().BeNull();
    }

    [Fact]
    public void Append_RepeatedSeq_IsDuplicateAndNotAppended()
    {
        var buffers = new RecordingBuffers(new FakeTimeProvider(Start));
        var id = Guid.NewGuid();
        buffers.Begin(id, Meta(Provider));
        buffers.Append(id, Provider, 0, [1, 2]);

        buffers.Append(id, Provider, 0, [1, 2]).Should().Be(new ChunkResult(ChunkStatus.Duplicate, 0));

        buffers.Take(id, Provider)!.Audio.Length.Should().Be(2);
    }

    [Fact]
    public void Append_SkippedSeq_IsGap()
    {
        var buffers = new RecordingBuffers(new FakeTimeProvider(Start));
        var id = Guid.NewGuid();
        buffers.Begin(id, Meta(Provider));

        buffers.Append(id, Provider, 1, [1, 2]).Should().Be(new ChunkResult(ChunkStatus.Gap, -1));
    }

    [Fact]
    public void Append_UnknownRecording_IsExpired()
    {
        var buffers = new RecordingBuffers(new FakeTimeProvider(Start));

        buffers.Append(Guid.NewGuid(), Provider, 0, [1, 2]).Status.Should().Be(ChunkStatus.Expired);
    }

    [Fact]
    public void OtherProvider_CannotBeginAppendPeekOrTake()
    {
        var buffers = new RecordingBuffers(new FakeTimeProvider(Start));
        var id = Guid.NewGuid();
        buffers.Begin(id, Meta(Provider));

        buffers.Begin(id, Meta(Other)).Should().Be(BeginStatus.Forbidden);
        buffers.Append(id, Other, 0, [1, 2]).Status.Should().Be(ChunkStatus.Forbidden);
        buffers.Peek(id, Other).Should().BeNull();
        buffers.Take(id, Other).Should().BeNull();
        buffers.Take(id, Provider).Should().NotBeNull();
    }

    [Fact]
    public void Begin_AgainBySameProvider_ResumesAndKeepsAudio()
    {
        var buffers = new RecordingBuffers(new FakeTimeProvider(Start));
        var id = Guid.NewGuid();
        buffers.Begin(id, Meta(Provider));
        buffers.Append(id, Provider, 0, [1, 2]);

        buffers.Begin(id, Meta(Provider)).Should().Be(BeginStatus.Resumed);

        buffers.Append(id, Provider, 1, [3, 4]).Status.Should().Be(ChunkStatus.Accepted);
    }

    [Fact]
    public void Append_BeyondMaxAudio_IsTooLong()
    {
        var buffers = new RecordingBuffers(new FakeTimeProvider(Start), maxAudioBytes: 4);
        var id = Guid.NewGuid();
        buffers.Begin(id, Meta(Provider));
        buffers.Append(id, Provider, 0, [1, 2, 3, 4]);

        buffers.Append(id, Provider, 1, [5, 6]).Should().Be(new ChunkResult(ChunkStatus.TooLong, 0));
    }

    [Fact]
    public void SweepExpired_After10IdleMinutes_DiscardsAndZeroes()
    {
        var clock = new FakeTimeProvider(Start);
        var buffers = new RecordingBuffers(clock);
        var idle = Guid.NewGuid();
        var active = Guid.NewGuid();
        buffers.Begin(idle, Meta(Provider));
        buffers.Begin(active, Meta(Provider));
        clock.Advance(TimeSpan.FromMinutes(9));
        buffers.Append(active, Provider, 0, [1, 2]);
        clock.Advance(TimeSpan.FromMinutes(2));

        buffers.SweepExpired().Should().Be(1);

        buffers.Peek(idle, Provider).Should().BeNull();
        buffers.Peek(active, Provider).Should().NotBeNull();
    }
}
