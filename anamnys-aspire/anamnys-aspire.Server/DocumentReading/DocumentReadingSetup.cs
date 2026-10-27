using Microsoft.Extensions.Http.Resilience;

namespace Anamnys.Server.DocumentReading;

public static class DocumentReadingSetup
{
    public static void AddDocumentReading(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<DocumentReadingOptions>(builder.Configuration.GetSection("DocumentReading"));
#pragma warning disable EXTEXP0001 // RemoveAllResilienceHandlers is experimental
        builder.Services.AddHttpClient(nameof(TessdataProvider), c => c.Timeout = TimeSpan.FromMinutes(10))
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
        builder.Services.AddSingleton<TessdataProvider>();
        builder.Services.AddSingleton<IOcrEngine, TesseractOcr>();
        builder.Services.AddSingleton<IPageRenderer, PdfiumPageRenderer>();
        builder.Services.AddSingleton<IDocumentReader, DocumentReader>();
        builder.Services.AddSingleton<ReadQueue>();
        builder.Services.AddHostedService<ReadWorker>();
    }
}
