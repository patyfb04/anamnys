using System.Threading.Channels;

namespace Anamnys.Server.Themes;

// "The vocabulary changed (or the app started): warm the suggester". One slot, extra requests are
// dropped, so a burst of vocabulary edits becomes a single re-warm. Request never blocks.
public sealed class ThemeWarmupSignal
{
    private readonly Channel<bool> channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });

    public void Request() => channel.Writer.TryWrite(true);

    public async Task WaitAsync(CancellationToken ct) => await channel.Reader.ReadAsync(ct);
}
