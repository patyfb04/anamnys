import { useEffect, useRef, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { notesApi } from "@anamnys/shared/api/notes";
import type { NoteFailureReason } from "@anamnys/shared/lib/types";
import { progressHub } from "@/lib/signalr";

const POLL_MS = 5000;

// Waits for the note: "sending" until finalize returns a note id, then queue/transcribing until
// ready (navigates to the note) or failed. The progress hub gives live queue position and a fast
// ready; polling the note is the fallback if the hub fails or drops a notification.
export default function ProcessingStep({ noteId, onRecordAgain }: { noteId: string | null; onRecordAgain: () => void }) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [position, setPosition] = useState(0);
  const [hubFailed, setHubFailed] = useState<NoteFailureReason | null>(null);
  const navigated = useRef(false);

  const poll = useQuery({
    queryKey: ["live-session-note", noteId],
    queryFn: () => notesApi.getDetail(noteId!),
    enabled: !!noteId,
    refetchInterval: (q) => (q.state.data && q.state.data.status !== "Processing" ? false : POLL_MS),
    gcTime: 0,
  });
  const polledFailure = poll.data?.status === "Failed" ? (poll.data.failureReason ?? "transcription_error") : null;
  const polledReady = poll.data?.status === "ReadyForReview";
  const failed = hubFailed ?? polledFailure;

  const goToNote = (id: string) => {
    if (navigated.current) return;
    navigated.current = true;
    void navigate({ to: "/notes/$noteId", params: { noteId: id } });
  };

  useEffect(() => {
    if (noteId && polledReady) goToNote(noteId);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [noteId, polledReady]);

  useEffect(() => {
    if (!noteId) return;
    let active = true;
    void (async () => {
      try {
        const hub = await progressHub.connect({
          onProgress: (id, _stage, queuePosition) => { if (id === noteId && active) setPosition(queuePosition); },
          onReady: (id) => { if (id === noteId && active) goToNote(noteId); },
          onFailed: (id, reason) => { if (id === noteId && active) setHubFailed(reason); },
        });
        if (!active || !hub) return;
        const snapshot = await hub.subscribe(noteId);
        if (!active || !snapshot) return;
        if (snapshot.status === "ReadyForReview") goToNote(noteId);
        else if (snapshot.status === "Failed") setHubFailed(snapshot.failureReason ?? "transcription_error");
        else setPosition(snapshot.queuePosition);
      } catch {
        // Hub unavailable: the polling fallback above keeps the step moving.
      }
    })();
    return () => { active = false; void progressHub.disconnect(); };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [noteId, navigate]);

  if (failed) {
    return (
      <div className="flex flex-col items-center gap-4">
        <p className="text-body-lg text-error">{t(`liveSession.failed.${failed}`)}</p>
        <button type="button" onClick={onRecordAgain} className="px-4 py-2 rounded-radii-lg bg-primary text-label-lg text-onPrimary">{t("liveSession.recordAgain")}</button>
      </div>
    );
  }
  return (
    <p className="text-center text-body-lg text-onSurfaceVariant">
      {!noteId ? t("liveSession.sending") : position > 0 ? t("liveSession.queued", { position }) : t("liveSession.transcribing")}
    </p>
  );
}
