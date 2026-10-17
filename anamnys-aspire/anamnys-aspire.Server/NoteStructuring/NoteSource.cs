namespace Anamnys.Server.NoteStructuring;

// What the text to structure came from; only the prompt's wording about its source differs.
public enum NoteSource { Dictation, Typed }

public static class NoteSources
{
    public static NoteSource Of(string inputMode) => inputMode == "Text" ? NoteSource.Typed : NoteSource.Dictation;
}
