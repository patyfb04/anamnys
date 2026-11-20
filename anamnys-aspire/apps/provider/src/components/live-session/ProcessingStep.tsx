import { useEffect, useRef, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { Link, useNavigate } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { notesApi } from "@anamnys/shared/api/notes";
import { pseudonymizationApi } from "@anamnys/shared/api/pseudonymization";
import { ApiError } from "@anamnys/shared/api/client";
import { toast } from "@anamnys/shared/ui/Toaster";
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
  const [retrying, setRetrying] = useState(false);
  const [gone, setGone] = useState(false);

  // A hub failure may arrive before the polled note shows the failure: refetch once to learn whether it is retryable.
  useEffect(() => {
    if (hubFailed === "pseudonymization_failed") void poll.refetch();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [hubFailed]);

  const retryProtection = async () => {
    if (!noteId) return;
    setRetrying(true);
    try {
      await pseudonymizationApi.retry(noteId);
      setHubFailed(null);
    } catch (e) {
      const status = e instanceof ApiError ? e.status : undefined;
      if (status === 404 || status === 409 || status === 410) setGone(true);
      else toast.error(t("pseudonymization.failed.retryFailed"));
    } finally {
      setRetrying(false);
      void poll.refetch();
    }
  };

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

  if (failed === "pseudonymization_failed" && noteId) {
    const retryable = poll.data?.status === "Failed" && !!poll.data.pseudonymizationRetryable && !gone;
    return (
      <div className="flex flex-col items-start gap-4">
        <p className="text-body-lg text-error">{t(`liveSession.failed.${failed}`)}</p>
        <div className="flex flex-wrap items-center gap-3">
          {retryable && (
            <button
              type="button"
              disabled={retrying}
              aria-busy={retrying}
              onClick={retryProtection}
              className="px-4 py-2 rounded-radii-lg bg-primary text-label-lg text-onPrimary disabled:opacity-60"
            >
              {t("pseudonymization.failed.retry")}
            </button>
          )}
          <Link to="/notes/$noteId" params={{ noteId }} className="px-4 py-2 rounded-radii-lg text-label-lg text-primary hover:bg-pastelLavender">
            {t("pseudonymization.openNote")}
          </Link>
          <button type="button" onClick={onRecordAgain} className="px-4 py-2 rounded-radii-lg text-label-lg text-primary hover:bg-pastelLavender">{t("liveSession.recordAgain")}</button>
        </div>
      </div>
    );
  }
  if (failed) {
    return (
      <div className="flex flex-col items-start gap-4">
        <p className="text-body-lg text-error">{t(`liveSession.failed.${failed}`)}</p>
        <button type="button" onClick={onRecordAgain} className="px-4 py-2 rounded-radii-lg bg-primary text-label-lg text-onPrimary">{t("liveSession.recordAgain")}</button>
      </div>
    );
  }
  return (
    <p className="text-body-lg text-onSurfaceVariant">
      {!noteId ? t("liveSession.sending") : position > 0 ? t("liveSession.queued", { position }) : t("liveSession.transcribing")}
    </p>
  );
}
