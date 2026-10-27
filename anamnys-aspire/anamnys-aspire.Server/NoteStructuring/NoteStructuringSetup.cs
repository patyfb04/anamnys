using Anamnys.Server.Themes;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace Anamnys.Server.NoteStructuring;

public static class NoteStructuringSetup
{
    public static void AddNoteStructuring(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<NoteStructuringOptions>(builder.Configuration.GetSection("NoteStructuring"));
        // The model download takes minutes: the standard handler's total timeout must not cap it.
#pragma warning disable EXTEXP0001 // RemoveAllResilienceHandlers is experimental
        builder.Services.AddHttpClient(nameof(LlmModelProvider), c => c.Timeout = TimeSpan.FromMinutes(60))
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
        builder.Services.AddSingleton<LlmModelProvider>();
        builder.Services.AddSingleton<INoteStructurer>(sp => new LlamaNoteStructurer(
            sp.GetRequiredService<LlmModelProvider>(), sp.GetRequiredService<IOptions<NoteStructuringOptions>>()));
        // Same weights as the structurer: the provider loads the model once per process.
        // One gate for every model run outside structuring: suggestions, locates and document facts (F-08).
        builder.Services.AddSingleton<LlmGate>();
        builder.Services.AddSingleton<IThemeSuggester>(sp => new LlamaThemeSuggester(
            sp.GetRequiredService<LlmModelProvider>(), sp.GetRequiredService<IOptions<NoteStructuringOptions>>(),
            sp.GetRequiredService<LlmGate>()));
        builder.Services.AddSingleton<ThemeWarmupSignal>();
        builder.Services.AddHostedService<ThemeWarmupService>();
        builder.Services.AddSingleton<ThemeLocateQueue>();
        builder.Services.AddHostedService<ThemeLocateWorker>();
    }
}
