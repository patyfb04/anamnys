using Anamnys.Server.NoteStructuring;
using FluentAssertions;

namespace Anamnys.Tests.DocumentFacts;

// The shared model gate with fake work: who gets it, in which order, and the yield between steps.
public class LlmGateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Release_InteractiveAndBackgroundWaiting_GrantsInteractiveFirst_ThenInArrivalOrder()
    {
        // Arrange
        using var gate = new LlmGate();
        await gate.WaitAsync(LlmPriority.Background, Ct);
        var order = new List<string>();
        async Task Run(string name, LlmPriority priority)
        {
            await gate.WaitAsync(priority, Ct);
            order.Add(name);
            gate.Release();
        }
        var background1 = Run("background 1", LlmPriority.Background);
        var background2 = Run("background 2", LlmPriority.Background);
        var interactive1 = Run("interactive 1", LlmPriority.Interactive);
        var interactive2 = Run("interactive 2", LlmPriority.Interactive);

        // Act
        gate.Release();
        await Task.WhenAll(background1, background2, interactive1, interactive2).WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // Assert
        order.Should().Equal("interactive 1", "interactive 2", "background 1", "background 2");
    }

    [Fact]
    public async Task WaitAsync_FreeGate_IsTakenAtOnce_ButNotAheadOfAWaitingInteractive()
    {
        // Arrange
        using var gate = new LlmGate();

        // Act
        var first = gate.WaitAsync(LlmPriority.Background, Ct);
        var interactive = gate.WaitAsync(LlmPriority.Interactive, Ct);
        var background = gate.WaitAsync(LlmPriority.Background, Ct);

        // Assert
        first.IsCompletedSuccessfully.Should().BeTrue();
        interactive.IsCompleted.Should().BeFalse();
        gate.InteractiveWaiting.Should().BeTrue();
        gate.Release();
        await interactive.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        background.IsCompleted.Should().BeFalse();
        gate.Release();
        await background.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        gate.Release();
    }

    [Fact]
    public async Task YieldAsync_BackgroundRunInSteps_LetsAnInteractiveRequestInAfterTheCurrentStep()
    {
        // Arrange: a background run of 5 steps; an interactive request arrives during step 2.
        using var gate = new LlmGate();
        var log = new List<string>();
        var interactiveArrived = new TaskCompletionSource();
        Task? interactive = null;
        async Task Background()
        {
            await gate.WaitAsync(LlmPriority.Background, Ct);
            try
            {
                for (var step = 1; step <= 5; step++)
                {
                    await gate.YieldAsync();
                    log.Add($"step {step}");
                    if (step == 2)
                    {
                        interactive = Interactive();
                        await interactiveArrived.Task;
                    }
                }
            }
            finally
            {
                gate.Release();
            }
        }
        async Task Interactive()
        {
            var waiting = gate.WaitAsync(LlmPriority.Interactive, Ct);
            interactiveArrived.SetResult();
            await waiting;
            log.Add("interactive");
            gate.Release();
        }

        // Act
        await Background().WaitAsync(TimeSpan.FromSeconds(10), Ct);
        await interactive!.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        // Assert
        log.Should().Equal("step 1", "step 2", "interactive", "step 3", "step 4", "step 5");
    }

    [Fact]
    public async Task YieldAsync_NobodyInteractiveWaiting_KeepsTheGate()
    {
        // Arrange
        using var gate = new LlmGate();
        await gate.WaitAsync(LlmPriority.Background, Ct);
        var background = gate.WaitAsync(LlmPriority.Background, Ct);

        // Act
        var yielded = await gate.YieldAsync();

        // Assert
        yielded.Should().BeFalse();
        background.IsCompleted.Should().BeFalse();
        gate.Release();
        await background.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        gate.Release();
    }

    [Fact]
    public async Task WaitAsync_CancelledWhileWaiting_LeavesTheQueue_AndTheNextWaiterGetsTheGate()
    {
        // Arrange
        using var gate = new LlmGate();
        await gate.WaitAsync(LlmPriority.Background, Ct);
        using var cancel = new CancellationTokenSource();
        var cancelled = gate.WaitAsync(LlmPriority.Interactive, cancel.Token);
        var next = gate.WaitAsync(LlmPriority.Background, Ct);

        // Act
        await cancel.CancelAsync();
        var thrown = async () => await cancelled;

        // Assert
        await thrown.Should().ThrowAsync<OperationCanceledException>();
        gate.InteractiveWaiting.Should().BeFalse();
        gate.Release();
        await next.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        gate.Release();
    }

    [Fact]
    public void Release_NotHeld_Throws()
    {
        // Arrange
        using var gate = new LlmGate();

        // Act
        var release = () => gate.Release();

        // Assert
        release.Should().Throw<SemaphoreFullException>();
    }
}
