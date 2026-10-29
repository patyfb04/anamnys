using Anamnys.Server.Auth;
using Anamnys.Server.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Anamnys.Server.LiveSession;

// design/specs/2026-10-10-live-session-transcription-design.md §4.1. Providers cookie only: any
// other realm is not an authenticated principal here (401), as on /api/phi.
[Authorize(AuthenticationSchemes = AuthSchemes.ProviderCookie)]
public sealed class TranscriptionHub(
    RecordingBuffers buffers, TranscriptionQueue queue, AnamnysDbContext db, TimeProvider clock) : Hub
{
    private Guid ProviderId => Context.User!.LocalId();

    public async Task<BeginAck> BeginRecording(Guid recordingId, Guid patientId, Guid? appointmentId, string format, string language)
    {
        var check = await LiveRecordings.CheckBeginAsync(db, ProviderId, patientId, appointmentId, format, language, Context.ConnectionAborted);
        if (check != BeginCheck.Ok)
        {
            return new BeginAck(check switch { BeginCheck.Invalid => "invalid", BeginCheck.Archived => "archived", _ => "not_found" });
        }
        var meta = new RecordingMeta(ProviderId, patientId, appointmentId, format, language, clock.GetUtcNow());
        return new BeginAck(buffers.Begin(recordingId, meta) == BeginStatus.Forbidden ? "forbidden" : "ok");
    }

    public ChunkAck SendAudioChunk(Guid recordingId, int seq, byte[] chunk)
    {
        var result = buffers.Append(recordingId, ProviderId, seq, chunk);
        Array.Clear(chunk);
        return new ChunkAck(result.Status switch
        {
            ChunkStatus.Accepted => "accepted",
            ChunkStatus.Duplicate => "duplicate",
            ChunkStatus.Gap => "gap",
            ChunkStatus.Expired => "buffer_expired",
            ChunkStatus.TooLong => "too_long",
            _ => "forbidden",
        }, result.LastSeq);
    }

    public async Task<FinalizeAck> FinalizeStream(Guid recordingId)
    {
        var result = await LiveRecordings.FinalizeAsync(db, buffers, queue, ProviderId, recordingId, clock.GetUtcNow(), Context.ConnectionAborted);
        return result.Status == FinalizeStatus.Ok
            ? new FinalizeAck("ok", result.NoteId)
            : new FinalizeAck("buffer_expired", null);
    }
}
