namespace Anamnys.Server.NoteStructuring;

public sealed class NoteStructuringOptions
{
    public string ModelDirectory { get; set; } = "";
    public Uri ModelBaseUrl { get; set; } = new("https://huggingface.co/Qwen/Qwen2.5-7B-Instruct-GGUF/resolve/main/");

    // The GGUF parts in split order; the first is the one llama.cpp is pointed at. No pinned
    // SHA-256, no model: the provider refuses to download or load.
    public List<ModelFile> Files { get; set; } = [];

    public uint ContextSize { get; set; } = 16384;
    public int Threads { get; set; }
    // Optional cap on generated tokens; 0 = no cap beyond what the context leaves after the prompt.
    public int MaxOutputTokens { get; set; }
    public float Temperature { get; set; } = 0.1f;

    // Theme suggestion keeps its own small context for the life of the process: the cached prompt
    // prefix (rules + vocabulary), the note text cut to what remains, and the output.
    public uint SuggestionContextSize { get; set; } = 4096;
    // Threads for suggestion; 0 = Threads (ResolvedThreads).
    public int SuggestionThreads { get; set; }
    // Load the model and build the suggestion cache in the background after startup (and after
    // vocabulary changes), so the first suggestion does not wait for it.
    public bool WarmSuggestionOnStart { get; set; } = true;

    public string ResolvedDirectory => ModelDirectory is { Length: > 0 } dir
        ? dir
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "anamnys", "llm");

    public int ResolvedThreads => Threads > 0 ? Threads : Math.Max(1, Environment.ProcessorCount - 1);

    public int ResolvedSuggestionThreads => SuggestionThreads > 0 ? SuggestionThreads : ResolvedThreads;

    public sealed class ModelFile
    {
        public string Name { get; set; } = "";
        public string Sha256 { get; set; } = "";
    }
}
