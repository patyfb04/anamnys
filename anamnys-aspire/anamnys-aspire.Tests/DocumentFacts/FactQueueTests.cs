using Anamnys.Server.DocumentFacts;
using FluentAssertions;

namespace Anamnys.Tests.DocumentFacts;

public class FactQueueTests
{
    [Fact]
    public void Enqueue_SameDocumentTwiceWhileQueued_QueuesItOnce()
    {
        // Arrange
        var queue = new FactQueue();
        var job = new FactJob(Guid.NewGuid(), Guid.NewGuid());

        // Act
        var first = queue.Enqueue(job);
        var second = queue.Enqueue(job);

        // Assert
        first.Should().BeTrue();
        second.Should().BeFalse();
        queue.Reader.TryRead(out _).Should().BeTrue();
        queue.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public void IsBusy_FromEnqueueUntilEnd_AndARunningDocumentCanBeQueuedAgain()
    {
        // Arrange
        var queue = new FactQueue();
        var job = new FactJob(Guid.NewGuid(), Guid.NewGuid());

        // Act / Assert
        queue.IsBusy(job.DocumentId).Should().BeFalse();
        queue.Enqueue(job);
        queue.IsBusy(job.DocumentId).Should().BeTrue();
        queue.Begin(job);
        queue.IsBusy(job.DocumentId).Should().BeTrue();
        queue.Enqueue(job).Should().BeTrue();
        queue.End(job);
        queue.IsBusy(job.DocumentId).Should().BeTrue();
        queue.Begin(job);
        queue.End(job);
        queue.IsBusy(job.DocumentId).Should().BeFalse();
    }
}
