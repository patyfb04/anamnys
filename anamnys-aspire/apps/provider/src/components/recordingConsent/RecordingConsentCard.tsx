import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Download, FileSignature, Loader2, RefreshCw, Send, ShieldOff } from "lucide-react";
import { recordingConsentApi, type ConsentEvent, type RecordingConsent } from "@anamnys/shared/api/recordingConsent";
import Badge, { type BadgeTone } from "@anamnys/shared/ui/Badge";
import { toast } from "@anamnys/shared/ui/Toaster";
import DetailCard from "@/components/patients/detail/DetailCard";
import ConfirmDialog from "@/components/patients/ConfirmDialog";
import Modal from "@/components/patients/Modal";
import { formatDate, formatDay, todayIso } from "@/components/patients/format";
import { consentKey, errorCode, useRecordingConsent } from "./consentModel";
import SendConsentModal from "./SendConsentModal";

const TONE: Record<RecordingConsent["state"], BadgeTone> = {
  none: "neutral",
  sent: "blue",
  signed: "mint",
  revoked: "pink",
  expired: "neutral",
};

const secondaryButton =
  "inline-flex items-center gap-1.5 rounded-radii-md border border-outlineVariant px-3 py-1.5 text-label-md text-primary hover:bg-pastelLavender disabled:opacity-60";
const primaryButton =
  "inline-flex items-center gap-2 rounded-radii-md bg-primary px-4 py-2 text-label-lg text-onPrimary hover:opacity-90 disabled:opacity-60";

// Patient page, Avaliação tab: state of the recording consent, its actions and history
// (design/specs/2026-11-15-recording-consent-design.md §6).
export default function RecordingConsentCard({ patientId, readOnly }: { patientId: string; readOnly: boolean }) {
  const { t, i18n } = useTranslation();
  const queryClient = useQueryClient();
  const query = useRecordingConsent(patientId);
  const [sending, setSending] = useState(false);
  const [revoking, setRevoking] = useState(false);
  const [renewing, setRenewing] = useState(false);
  const [downloading, setDownloading] = useState(false);

  const refresh = () => queryClient.invalidateQueries({ queryKey: consentKey(patientId) });
  const fail = (e: unknown) => {
    toast.error(t(`recordingConsent.send.errors.${errorCode(e) ?? "unknown"}`, { defaultValue: t("recordingConsent.card.actionFailed") }));
    void refresh();
  };
  const resend = useMutation({
    mutationFn: (id: string) => recordingConsentApi.resend(id),
    onSuccess: async () => {
      await refresh();
      toast.success(t("recordingConsent.card.resent"));
    },
    onError: fail,
  });
  const renew = useMutation({
    mutationFn: (id: string) => recordingConsentApi.renew(id),
    onSuccess: async () => {
      setRenewing(false);
      await refresh();
      toast.success(t("recordingConsent.card.sent"));
    },
    onError: (e) => {
      setRenewing(false);
      fail(e);
    },
  });

  const c = query.data;
  const download = async () => {
    if (!c?.id) return;
    setDownloading(true);
    try {
      const url = URL.createObjectURL(await recordingConsentApi.document(c.id));
      const a = document.createElement("a");
      a.href = url;
      a.download = "termo-consentimento-gravacao.pdf";
      a.click();
      window.setTimeout(() => URL.revokeObjectURL(url), 60_000);
    } catch {
      toast.error(t("recordingConsent.card.downloadFailed"));
    } finally {
      setDownloading(false);
    }
  };

  const blocked = c?.sendBlockedBy ?? null;
  const olderVersion = !!c && c.version !== null && c.version !== c.currentVersion;
  // A link-expired consent of an older term version cannot be resent (the server refuses): a new term is sent instead.
  const canSendNew =
    !readOnly &&
    !!c &&
    (c.state === "none" ||
      c.state === "revoked" ||
      (c.state === "expired" && (c.expiredReason !== "link" || olderVersion)));
  const canResend = !readOnly && !!c?.id && !olderVersion && (c.state === "sent" || (c.state === "expired" && c.expiredReason === "link"));
  const outdated = !!c && (c.state === "signed" || c.state === "sent") && olderVersion;
  const canRevoke = !!c?.id && (c.state === "sent" || c.state === "signed");
  const showAssent = !readOnly && !!c?.id && c.isMinor && (c.state === "sent" || c.state === "signed");

  return (
    <DetailCard title={t("recordingConsent.card.title")} icon={<FileSignature size={20} />}>
      {query.isError ? (
        <p className="text-body-md text-error">{t("recordingConsent.card.loadFailed")}</p>
      ) : !c ? null : (
        <div className="flex flex-col gap-4">
          <div className="flex flex-wrap items-center gap-2">
            <Badge label={t(`recordingConsent.state.${c.state}`)} tone={TONE[c.state]} />
          </div>

          <StateDetails c={c} locale={i18n.language} />

          <p className={`text-body-sm ${c.canRecord ? "text-onSurfaceVariant" : "text-onSurface"}`}>
            {t(`recordingConsent.card.capture.${c.canRecord ? "ok" : c.reason}`, { defaultValue: "" })}
          </p>

          {outdated && (
            <p className="rounded-radii-md bg-pastelLavender px-3 py-2 text-body-md text-onPastelLavender">
              {t("recordingConsent.card.newVersion", { version: c.version, current: c.currentVersion })}
            </p>
          )}

          {(canSendNew || canResend || (!readOnly && outdated)) && blocked && (
            <p role="status" className="rounded-radii-md bg-errorContainer px-3 py-2 text-body-md text-onErrorContainer">
              {t(`recordingConsent.blocked.${blocked}`)}
            </p>
          )}

          <div className="flex flex-wrap items-center gap-2">
            {canSendNew && (
              <button type="button" disabled={blocked !== null} onClick={() => setSending(true)} className={primaryButton}>
                <Send size={16} aria-hidden />
                {t("recordingConsent.card.send")}
              </button>
            )}
            {canResend && (
              <button type="button" disabled={blocked !== null || resend.isPending} onClick={() => resend.mutate(c.id!)} className={secondaryButton}>
                {resend.isPending ? <Loader2 size={14} className="animate-spin" aria-hidden /> : <RefreshCw size={14} aria-hidden />}
                {t("recordingConsent.card.resend")}
              </button>
            )}
            {!readOnly && outdated && (
              <button type="button" disabled={blocked !== null || renew.isPending} onClick={() => setRenewing(true)} className={secondaryButton}>
                <RefreshCw size={14} aria-hidden />
                {t("recordingConsent.card.renew")}
              </button>
            )}
            {c.hasDocument && (
              <button type="button" disabled={downloading} onClick={() => void download()} className={secondaryButton}>
                <Download size={14} aria-hidden />
                {t("recordingConsent.card.download")}
              </button>
            )}
            {canRevoke && (
              <button
                type="button"
                onClick={() => setRevoking(true)}
                className="inline-flex items-center gap-1.5 rounded-radii-md px-3 py-1.5 text-label-md text-error hover:bg-errorContainer"
              >
                <ShieldOff size={14} aria-hidden />
                {t("recordingConsent.card.revoke")}
              </button>
            )}
          </div>

          {showAssent && <AssentForm key={c.assentRecordedAt ?? "none"} consent={c} patientId={patientId} locale={i18n.language} />}

          <History events={c.events} locale={i18n.language} />
        </div>
      )}

      {sending && <SendConsentModal patientId={patientId} onClose={() => setSending(false)} />}
      {revoking && c?.id && <RevokeModal consentId={c.id} patientId={patientId} onClose={() => setRevoking(false)} />}
      <ConfirmDialog
        open={renewing}
        onOpenChange={setRenewing}
        title={t("recordingConsent.card.renew")}
        description={t("recordingConsent.card.newVersion", { version: c?.version, current: c?.currentVersion })}
        confirmLabel={t("recordingConsent.card.renew")}
        pending={renew.isPending}
        onConfirm={() => c?.id && renew.mutate(c.id)}
      />
    </DetailCard>
  );
}

function StateDetails({ c, locale }: { c: RecordingConsent; locale: string }) {
  const { t } = useTranslation();
  const lines: string[] = [];
  if (c.state === "none") {
    lines.push(t("recordingConsent.card.none"));
  } else {
    if (c.sentAt) lines.push(t("recordingConsent.card.sentAt", { date: formatDate(c.sentAt, locale) }));
    if (c.state === "sent") {
      lines.push(
        c.linkExpiresAt && new Date(c.linkExpiresAt) > new Date()
          ? t("recordingConsent.card.linkUntil", { date: formatDate(c.linkExpiresAt, locale) })
          : t("recordingConsent.card.linkExpired"),
      );
    }
    if (c.signedAt && c.signerName) {
      lines.push(t("recordingConsent.card.signedAt", { date: formatDate(c.signedAt, locale) }));
      const role =
        c.signerRole === "guardian"
          ? t("recordingConsent.card.signerGuardian", { relationship: c.guardianRelationship ?? "" })
          : t("recordingConsent.card.signerPatient");
      lines.push(t("recordingConsent.card.signer", { name: c.signerName, role }));
    }
    if (c.state === "revoked") {
      if (c.revokedAt) lines.push(t("recordingConsent.card.revokedAt", { date: formatDate(c.revokedAt, locale) }));
      if (c.revokedBy) lines.push(t(`recordingConsent.card.revokedBy.${c.revokedBy}`));
      if (c.revokeReason) lines.push(t("recordingConsent.card.revokeReason", { reason: c.revokeReason }));
    }
    if (c.state === "expired" && c.expiredReason) lines.push(t(`recordingConsent.expired.${c.expiredReason}`));
    if (c.providerNote) lines.push(t("recordingConsent.card.providerNote", { note: c.providerNote }));
  }
  return (
    <ul className="flex flex-col gap-1 text-body-md text-onSurfaceVariant">
      {lines.map((l, i) => (
        <li key={i}>{l}</li>
      ))}
    </ul>
  );
}

function AssentForm({ consent, patientId, locale }: { consent: RecordingConsent; patientId: string; locale: string }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [date, setDate] = useState(consent.assentRecordedAt ?? todayIso());
  const [note, setNote] = useState(consent.assentNote ?? "");
  const [editing, setEditing] = useState(!consent.assentRecordedAt);
  const save = useMutation({
    mutationFn: () => recordingConsentApi.assent(consent.id!, date, note.trim() || null),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: consentKey(patientId) });
      toast.success(t("recordingConsent.assent.saved"));
      setEditing(false);
    },
    onError: (e) => {
      const code = errorCode(e);
      toast.error(t(`recordingConsent.assent.errors.${code ?? "unknown"}`, { defaultValue: t("recordingConsent.card.actionFailed") }));
    },
  });

  return (
    <section className="flex flex-col gap-3 rounded-radii-lg border border-outlineVariant p-4">
      <h3 className="text-label-lg text-onSurface">{t("recordingConsent.assent.title")}</h3>
      {!editing && consent.assentRecordedAt ? (
        <div className="flex flex-wrap items-center justify-between gap-2">
          <p className="text-body-md text-onSurfaceVariant">
            {t("recordingConsent.assent.recorded", { date: formatDay(consent.assentRecordedAt, locale) })}
            {consent.assentNote ? ` ${consent.assentNote}` : ""}
          </p>
          <button type="button" onClick={() => setEditing(true)} className={secondaryButton}>
            {t("recordingConsent.assent.update")}
          </button>
        </div>
      ) : (
        <>
          <p className="text-body-sm text-onSurfaceVariant">{t("recordingConsent.assent.hint")}</p>
          <div className="flex flex-col gap-3 sm:flex-row">
            <label className="flex flex-col gap-1 text-label-md text-onSurfaceVariant">
              {t("recordingConsent.assent.date")}
              <input
                type="date"
                value={date}
                max={todayIso()}
                onChange={(e) => setDate(e.target.value)}
                className="rounded-radii-md border border-outlineVariant bg-surfaceContainerLowest px-3 py-2 text-body-md text-onSurface outline-none focus:border-primary"
              />
            </label>
            <label className="flex flex-1 flex-col gap-1 text-label-md text-onSurfaceVariant">
              {t("recordingConsent.assent.note")}
              <input
                type="text"
                value={note}
                maxLength={500}
                onChange={(e) => setNote(e.target.value)}
                className="rounded-radii-md border border-outlineVariant bg-surfaceContainerLowest px-3 py-2 text-body-md text-onSurface outline-none focus:border-primary"
              />
            </label>
          </div>
          <div>
            <button type="button" disabled={!date || save.isPending} onClick={() => save.mutate()} className={primaryButton}>
              {t("recordingConsent.assent.save")}
            </button>
          </div>
        </>
      )}
    </section>
  );
}

function RevokeModal({ consentId, patientId, onClose }: { consentId: string; patientId: string; onClose: () => void }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [reason, setReason] = useState("");
  const revoke = useMutation({
    mutationFn: () => recordingConsentApi.revoke(consentId, reason.trim() || null),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: consentKey(patientId) });
      toast.success(t("recordingConsent.revoke.done"));
      onClose();
    },
    onError: (e) => {
      toast.error(t(`recordingConsent.send.errors.${errorCode(e) ?? "unknown"}`, { defaultValue: t("recordingConsent.card.actionFailed") }));
      void queryClient.invalidateQueries({ queryKey: consentKey(patientId) });
    },
  });

  return (
    <Modal
      title={t("recordingConsent.revoke.title")}
      onClose={onClose}
      footer={
        <>
          <button type="button" onClick={onClose} className="rounded-radii-md px-4 py-2 text-label-lg text-onSurfaceVariant hover:bg-pastelLavender">
            {t("common.cancel")}
          </button>
          <button
            type="button"
            disabled={revoke.isPending}
            onClick={() => revoke.mutate()}
            className="rounded-radii-md bg-error px-4 py-2 text-label-lg text-onError disabled:opacity-50"
          >
            {t("recordingConsent.revoke.confirm")}
          </button>
        </>
      }
    >
      <div className="flex flex-col gap-4 p-5">
        <p className="text-body-md text-onSurface">{t("recordingConsent.revoke.body")}</p>
        <label className="flex flex-col gap-1 text-label-md text-onSurfaceVariant">
          {t("recordingConsent.revoke.reason")}
          <textarea
            value={reason}
            onChange={(e) => setReason(e.target.value)}
            rows={3}
            maxLength={500}
            className="w-full resize-y rounded-radii-md border border-outlineVariant bg-surfaceContainerLowest px-3 py-2 text-body-md text-onSurface outline-none focus:border-primary"
          />
        </label>
      </div>
    </Modal>
  );
}

function History({ events, locale }: { events: ConsentEvent[]; locale: string }) {
  const { t } = useTranslation();
  if (events.length === 0) return null;
  const label = (e: ConsentEvent) =>
    e.toState === "assent"
      ? t("recordingConsent.event.assent")
      : ["sent", "signed", "revoked", "expired"].includes(e.toState)
        ? t(`recordingConsent.event.${e.toState}`)
        : t("recordingConsent.event.other", { from: e.fromState, to: e.toState });
  return (
    <div className="flex flex-col gap-2">
      <h3 className="text-label-lg text-onSurfaceVariant">{t("recordingConsent.card.history")}</h3>
      <ul className="flex flex-col gap-1 text-body-sm text-onSurfaceVariant">
        {events.map((e, i) => (
          <li key={i}>
            {formatDate(e.at, locale)} · {label(e)} ({t(`recordingConsent.event.by.${e.actorKind}`)})
          </li>
        ))}
      </ul>
    </div>
  );
}
