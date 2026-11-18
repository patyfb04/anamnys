using Anamnys.Server.Auth;
using Anamnys.Server.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Anamnys.Server.LiveSession;

// design/specs/2026-10-10-live-session-transcription-design.md §4.1. Providers cookie only: any
// other realm is not an authenticated principal here (401), as on /api/phi.
[Authorize(AuthenticationSchemes = AuthSchemes.ProviderCookie)]
public sealed class TranscriptionHub(
    RecordingBuffers buffers, TranscriptionQueue queue, AnamnysDbContext db, TimeProvider clock, ILogger<TranscriptionHub> logger) : Hub
{
    private Guid ProviderId => Context.User!.LocalId();

    public async Task<BeginAck> BeginRecording(Guid recordingId, Guid patientId, Guid? appointmentId, string format, string language,
        string mode, bool dictationConfirmed, string source)
    {
        var result = await LiveRecordings.CheckBeginAsync(
            db, ProviderId, patientId, appointmentId, format, language, mode, dictationConfirmed, source, clock.GetUtcNow(),
            Context.ConnectionAborted);
        if (result.Check != BeginCheck.Ok)
        {
            return new BeginAck(result.Check switch
            {
                BeginCheck.Invalid => "invalid",
                BeginCheck.Archived => "archived",
                BeginCheck.ConsentRequired => "consent_required",
                BeginCheck.DictationNotConfirmed => "dictation_not_confirmed",
                _ => "not_found",
            }, result.Reason);
        }
        var meta = LiveRecordings.NewMeta(ProviderId, patientId, appointmentId, format, language, mode, source, clock.GetUtcNow());
        return new BeginAck(buffers.Begin(recordingId, meta) switch
        {
            BeginStatus.Forbidden => "forbidden",
            BeginStatus.ModeMismatch => "invalid",
            _ => "ok",
        });
    }

    // Changes the call's audio method inside the same recording (design/specs/2026-11-16-transient-audio-tab-capture-design.md §2.2).
    public async Task<SwitchAck> SwitchSource(Guid recordingId, string source)
    {
        var status = await LiveRecordings.SwitchSourceAsync(
            db, buffers, ProviderId, recordingId, source, clock.GetUtcNow(), Context.ConnectionAborted);
        return new SwitchAck(status switch
        {
            SwitchStatus.Ok => "ok",
            SwitchStatus.Invalid => "invalid",
            SwitchStatus.Expired => "expired",
            _ => "forbidden",
        });
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
        var result = await LiveRecordings.FinalizeAsync(db, buffers, queue, ProviderId, recordingId, clock.GetUtcNow(), Context.ConnectionAborted, logger);
        return result.Status == FinalizeStatus.Ok
            ? new FinalizeAck("ok", result.NoteId)
            : new FinalizeAck("buffer_expired", null);
    }
}
