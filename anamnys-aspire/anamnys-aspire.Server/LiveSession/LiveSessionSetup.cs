using Microsoft.Extensions.Http.Resilience;

namespace Anamnys.Server.LiveSession;

public static class LiveSessionSetup
{
    public static void AddLiveSession(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<LiveSessionOptions>(builder.Configuration.GetSection("LiveSession"));
        // The model download takes minutes: the standard handler's total timeout must not cap it.
#pragma warning disable EXTEXP0001 // RemoveAllResilienceHandlers is experimental
        builder.Services.AddHttpClient(nameof(WhisperModelProvider), c => c.Timeout = TimeSpan.FromMinutes(30))
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
        builder.Services.AddSingleton<WhisperModelProvider>();
        builder.Services.AddSingleton<ITranscriber, WhisperTranscriber>();
        builder.Services.AddSingleton<RecordingBuffers>();
        builder.Services.AddSingleton<TranscriptionQueue>();
        builder.Services.AddHostedService<OrphanNoteSweep>();
        builder.Services.AddHostedService<TranscriptionWorker>();
        builder.Services.AddHostedService<RecordingBufferSweeper>();
        builder.Services.AddSignalR();
    }

    public static void MapLiveSessionHubs(this WebApplication app)
    {
        app.MapHub<TranscriptionHub>("/hubs/transcription");
        app.MapHub<ProgressHub>("/hubs/progress");
    }
}
