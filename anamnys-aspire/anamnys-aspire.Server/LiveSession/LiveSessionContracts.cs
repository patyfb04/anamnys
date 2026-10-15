namespace Anamnys.Server.LiveSession;

public sealed record BeginAck(string Status);

public sealed record ChunkAck(string Status, int LastSeq);

public sealed record FinalizeAck(string Status, Guid? NoteId);

public sealed record NoteSnapshot(string Status, int QueuePosition, string? FailureReason);
