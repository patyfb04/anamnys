using Tesseract;

namespace Anamnys.Server.DocumentReading;

public interface IOcrEngine
{
    // The text Tesseract reads from a PNG or JPEG image, in Portuguese.
    Task<string> RecognizeAsync(byte[] image, CancellationToken ct);
}

// Tesseract 5 in process (LSTM engine, tessdata_fast "por"). One engine for the process, used by one
// caller at a time: the engine is not thread-safe.
public sealed class TesseractOcr(TessdataProvider tessdata) : IOcrEngine, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TesseractEngine? _engine;

    public async Task<string> RecognizeAsync(byte[] image, CancellationToken ct)
    {
        var directory = await tessdata.GetDirectoryAsync(ct);
        await _gate.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                _engine ??= new TesseractEngine(directory, TessdataProvider.Language, EngineMode.LstmOnly);
                using var pix = Pix.LoadFromMemory(image);
                using var page = _engine.Process(pix, PageSegMode.Auto);
                return page.GetText() ?? "";
            }, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _engine?.Dispose();
}
