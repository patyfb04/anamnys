using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Anamnys.Api.Hubs;

/// <summary>
/// Job progress hub. Clients subscribe to a specific jobId group
/// to receive pipeline progress, completion, and error events.
/// </summary>
[Authorize]
public class ProgressHub : Hub
{
    private readonly ILogger<ProgressHub> _logger;

    public ProgressHub(ILogger<ProgressHub> logger) => _logger = logger;

    /// <summary>Subscribe to progress events for a specific pipeline job.</summary>
    public async Task SubscribeToJob(string jobId)
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, jobId);
        _logger.LogInformation("Client {ConnId} subscribed to job {JobId}", Context.ConnectionId, jobId);
    }

    /// <summary>
    /// Removes the client from a job's SignalR group.
    /// Called by the mobile app when the user navigates away from the progress screen
    /// to stop receiving events for a job they're no longer watching.
    /// </summary>
    public async Task UnsubscribeFromJob(string jobId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, jobId);
    }
}
