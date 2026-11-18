namespace Anamnys.Server.LiveSession;

public sealed record BeginAck(string Status, string? Reason = null);

public sealed record ChunkAck(string Status, int LastSeq);

public sealed record FinalizeAck(string Status, Guid? NoteId);

// Status: ok | invalid | expired | forbidden.
public sealed record SwitchAck(string Status);

public sealed record NoteSnapshot(string Status, int QueuePosition, string? FailureReason);
