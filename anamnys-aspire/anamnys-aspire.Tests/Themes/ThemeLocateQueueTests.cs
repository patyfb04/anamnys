using Anamnys.Server.Themes;
using FluentAssertions;

namespace Anamnys.Tests.Themes;

public class ThemeLocateQueueTests
{
    private static readonly Guid Provider = Guid.NewGuid();
    private static readonly Guid Note = Guid.NewGuid();

    private static List<ThemeLocateJob> Drain(ThemeLocateQueue queue)
    {
        var jobs = new List<ThemeLocateJob>();
        while (queue.Reader.TryRead(out var job))
        {
            jobs.Add(job);
        }
        return jobs;
    }

    [Fact]
    public void Enqueue_SamePendingJobTwice_QueuesItOnce()
    {
        // Arrange
        var queue = new ThemeLocateQueue();
        var tag = Guid.NewGuid();

        // Act
        var first = queue.Enqueue(new ThemeLocateJob(Provider, Note, tag));
        var second = queue.Enqueue(new ThemeLocateJob(Provider, Note, tag));
        var other = queue.Enqueue(new ThemeLocateJob(Provider, Note, Guid.NewGuid()));

        // Assert
        first.Should().BeTrue();
        second.Should().BeFalse();
        other.Should().BeTrue();
        Drain(queue).Should().HaveCount(2);
    }

    [Fact]
    public void NoteWideJob_PendingWithSectionOnlyTagJob_DoesNotAbsorbIt()
    {
        // Arrange
        var queue = new ThemeLocateQueue();
        var tag = Guid.NewGuid();
        queue.Enqueue(new ThemeLocateJob(Provider, Note));

        // Act
        var queued = queue.Enqueue(new ThemeLocateJob(Provider, Note, tag, SectionOnly: true));
        var jobs = Drain(queue);
        var wideRuns = queue.Begin(jobs[0]);
        var sectionRuns = queue.Begin(jobs[1]);

        // Assert
        queued.Should().BeTrue();
        jobs.Should().HaveCount(2);
        wideRuns.Should().BeTrue();
        sectionRuns.Should().BeTrue();
    }

    [Fact]
    public void Begin_RemovesThePendingKey_SoTheJobCanBeQueuedAgain()
    {
        // Arrange
        var queue = new ThemeLocateQueue();
        var job = new ThemeLocateJob(Provider, Note, Guid.NewGuid());
        queue.Enqueue(job);
        var read = Drain(queue).Single();

        // Act
        var run = queue.Begin(read);
        var again = queue.Enqueue(job);

        // Assert
        run.Should().BeTrue();
        again.Should().BeTrue();
    }

    [Fact]
    public void NoteWideJob_AbsorbsTagJobsOfThatNote()
    {
        // Arrange
        var queue = new ThemeLocateQueue();
        var before = new ThemeLocateJob(Provider, Note, Guid.NewGuid());
        var noteWide = new ThemeLocateJob(Provider, Note);
        var otherNote = new ThemeLocateJob(Provider, Guid.NewGuid(), Guid.NewGuid());

        // Act
        queue.Enqueue(before);
        queue.Enqueue(noteWide);
        var after = queue.Enqueue(new ThemeLocateJob(Provider, Note, Guid.NewGuid()));
        queue.Enqueue(otherNote);
        var jobs = Drain(queue);
        var runs = jobs.Select(j => (j, run: queue.Begin(j))).ToList();

        // Assert: the tag job queued after the note-wide one is dropped; the one before it is skipped.
        after.Should().BeFalse();
        jobs.Should().Equal(before, noteWide, otherNote);
        runs.Single(r => r.j == before).run.Should().BeFalse();
        runs.Single(r => r.j == noteWide).run.Should().BeTrue();
        runs.Single(r => r.j == otherNote).run.Should().BeTrue();
    }
}
