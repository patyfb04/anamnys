using Anamnys.Server.Themes;
using FluentAssertions;

namespace Anamnys.Tests.Themes;

// Pure: no host, no model.
public class ThemeWarmupSignalTests
{
    [Fact]
    public async Task WaitAsync_NoRequest_StaysPending()
    {
        // Arrange
        var signal = new ThemeWarmupSignal();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        // Act
        var wait = () => signal.WaitAsync(cts.Token);

        // Assert
        await wait.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task WaitAsync_BurstOfRequests_CoalescesIntoOneWake()
    {
        // Arrange
        var signal = new ThemeWarmupSignal();
        var ct = TestContext.Current.CancellationToken;

        // Act
        signal.Request();
        signal.Request();
        signal.Request();
        await signal.WaitAsync(ct);
        var second = signal.WaitAsync(ct);
        await Task.Delay(50, ct);

        // Assert
        second.IsCompleted.Should().BeFalse();
        signal.Request();
        await second.WaitAsync(TimeSpan.FromSeconds(5), ct);
    }
}
