namespace Anamnys.Server.NoteStructuring;

public enum LlmPriority
{
    // Someone is waiting on the screen (the "Sugerir temas" dialog).
    Interactive,
    // Nobody waits for it right now (document fact extraction, background locates, structuring proposals, warm-up).
    Background,
}

// The single gate for model runs that share the process's weights outside structuring: theme
// suggestion and locate (LlamaThemeSuggester) and document fact extraction (LlamaFactExtractor). One
// holder at a time. When it is released, a waiting interactive request goes before any background one
// (in arrival order within a priority). A long background run calls YieldAsync between its steps: when
// an interactive request is waiting, the gate passes to it and the run resumes after it, so the dialog
// waits at most one step of an extraction (design/specs/2026-10-26-document-facts-design.md §1).
// Registered as a singleton; a component built without one (tests) gets its own.
public sealed class LlmGate : IDisposable
{
    private readonly Lock sync = new();
    private readonly LinkedList<TaskCompletionSource> interactive = [];
    private readonly LinkedList<TaskCompletionSource> background = [];
    private bool held;

    public bool InteractiveWaiting
    {
        get
        {
            lock (sync)
            {
                return interactive.Count > 0;
            }
        }
    }

    public async Task WaitAsync(LlmPriority priority, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LinkedList<TaskCompletionSource> queue;
        LinkedListNode<TaskCompletionSource> node;
        lock (sync)
        {
            // Free, and nobody of the same or a higher priority is waiting: taken at once.
            if (!held && interactive.Count == 0 && (priority == LlmPriority.Interactive || background.Count == 0))
            {
                held = true;
                return;
            }
            queue = priority == LlmPriority.Interactive ? interactive : background;
            node = queue.AddLast(waiter);
        }
        // A cancelled waiter leaves the queue; one already granted keeps the gate (the caller releases it).
        using var registration = ct.Register(() =>
        {
            bool removed;
            lock (sync)
            {
                removed = node.List is not null && RemoveNode(queue, node);
            }
            if (removed)
            {
                waiter.TrySetCanceled(ct);
            }
        });
        await waiter.Task;
    }

    // Hands the gate to the next waiter (interactive first) or frees it.
    public void Release()
    {
        TaskCompletionSource? next;
        lock (sync)
        {
            if (!held)
            {
                throw new SemaphoreFullException("The model gate is not held.");
            }
            var list = interactive.Count > 0 ? interactive : background.Count > 0 ? background : null;
            next = list?.First!.Value;
            if (list is not null)
            {
                list.RemoveFirst();
            }
            else
            {
                held = false;
            }
        }
        next?.TrySetResult();
    }

    // Called by a background holder between steps: when an interactive request is waiting, it runs first
    // and the caller holds the gate again on return. The wait to get it back is not cancellable, so the
    // caller always holds the gate when this returns (its finally releases it); it checks ct afterwards.
    // True when the gate was passed on.
    public async Task<bool> YieldAsync()
    {
        if (!InteractiveWaiting)
        {
            return false;
        }
        Release();
        await WaitAsync(LlmPriority.Background, CancellationToken.None);
        return true;
    }

    public void Dispose()
    {
    }

    private static bool RemoveNode(LinkedList<TaskCompletionSource> queue, LinkedListNode<TaskCompletionSource> node)
    {
        if (node.List != queue)
        {
            return false;
        }
        queue.Remove(node);
        return true;
    }
}
