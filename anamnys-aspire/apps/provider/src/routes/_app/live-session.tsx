import { createFileRoute, useBlocker } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import PrepareStep from "@/components/live-session/PrepareStep";
import RecordingStep from "@/components/live-session/RecordingStep";
import ProcessingStep from "@/components/live-session/ProcessingStep";
import { useLiveRecorder } from "@/hooks/useLiveRecorder";

export const Route = createFileRoute("/_app/live-session")({
  component: LiveSessionPage,
});

// Live dictation to transcript (design/specs/2026-10-10-live-session-transcription-design.md section 6).
// No PHI in the URL: patient and appointment stay in component state.
function LiveSessionPage() {
  const { t } = useTranslation();
  const recorder = useLiveRecorder();
  const holdsAudio = recorder.state === "recording" || recorder.state === "paused" || recorder.state === "finalizing";
  const processing = recorder.state === "finalizing" || recorder.state === "done";

  useBlocker({
    shouldBlockFn: () => holdsAudio && !window.confirm(t("liveSession.leaveWarning")),
    enableBeforeUnload: () => holdsAudio,
  });

  return (
    <div className="p-4 md:p-8">
      <h1 className="mb-6 text-headline-sm">{t("liveSession.title")}</h1>
      {recorder.error && recorder.error !== "too_long" && (
        <p className="mb-4 text-body-md text-error">{t(`liveSession.errors.${recorder.error}`)}</p>
      )}
      {recorder.error === "too_long" && processing && (
        <p className="mb-4 text-body-md text-onSurfaceVariant">{t("liveSession.errors.too_long")}</p>
      )}
      {(recorder.state === "idle" || recorder.state === "error") && <PrepareStep onStart={(o) => void recorder.start(o)} />}
      {(recorder.state === "recording" || recorder.state === "paused") && (
        <RecordingStep
          state={recorder.state}
          connection={recorder.connection}
          elapsedMs={recorder.elapsedMs}
          level={recorder.level}
          onPause={recorder.pause}
          onResume={recorder.resume}
          onStop={() => void recorder.stop()}
        />
      )}
      {processing && <ProcessingStep noteId={recorder.noteId} onRecordAgain={recorder.reset} />}
    </div>
  );
}
