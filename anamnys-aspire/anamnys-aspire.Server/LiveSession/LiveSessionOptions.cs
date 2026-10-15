namespace Anamnys.Server.LiveSession;

public sealed class LiveSessionOptions
{
    public string Model { get; set; } = "medium";
    public string ModelSha256 { get; set; } = "";
    public string ModelDirectory { get; set; } = "";
    public Uri ModelBaseUrl { get; set; } = new("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/");

    public string ResolvedDirectory => ModelDirectory is { Length: > 0 } dir
        ? dir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "anamnys", "whisper");
}
