namespace Anamnys.Domain.Enums;

public enum InputMode
{
    VoiceBatch,       // Mode A: post-appointment dictation (Whisper batch)
    VoiceLive,        // Mode B: real-time live dictation (Whisper streaming)
    VoiceAppointment, // Mode C: full appointment recording + diarization (Phase 2)
    Text              // Mode D: typed rough notes
}
