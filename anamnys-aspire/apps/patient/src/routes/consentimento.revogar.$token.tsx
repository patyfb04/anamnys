import { useEffect, useState, type FormEvent } from "react";
import { createFileRoute } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { publicConsentApi, type PublicRevokeView } from "@anamnys/shared/api/publicConsent";
import ConsentShell from "@/components/consent/ConsentShell";
import { failureCode, fieldClass, isDeadLink } from "@/components/consent/consentHelpers";

// Public revocation page, linked from the signed-copy e-mail. Shows the signed term (the server masks the date of
// birth in it) and asks the date of birth again; this page never shows or logs it.
// See design/specs/2026-11-15-recording-consent-design.md §4.
export const Route = createFileRoute("/consentimento/revogar/$token")({ component: ConsentRevokePage });

type Load = { kind: "loading" } | { kind: "ready"; view: PublicRevokeView } | { kind: "dead" } | { kind: "error" };

function ConsentRevokePage() {
  const { token } = Route.useParams();
  const { t, i18n } = useTranslation();
  const [load, setLoad] = useState<Load>({ kind: "loading" });
  const [attempt, setAttempt] = useState(0);
  const [birthDate, setBirthDate] = useState("");
  const [submitting, setSubmitting] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);
  const [done, setDone] = useState(false);

  useEffect(() => {
    let active = true;
    setLoad({ kind: "loading" });
    publicConsentApi
      .revokeView(token)
      .then((view) => active && setLoad({ kind: "ready", view }))
      .catch((e) => active && setLoad(isDeadLink(e) ? { kind: "dead" } : { kind: "error" }));
    return () => {
      active = false;
    };
  }, [token, attempt]);

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (!birthDate || submitting) return;
    setSubmitting(true);
    setFailure(null);
    try {
      await publicConsentApi.revoke(token, birthDate);
      setBirthDate("");
      setDone(true);
    } catch (err) {
      if (isDeadLink(err)) setLoad({ kind: "dead" });
      else setFailure(failureCode(err));
    } finally {
      setSubmitting(false);
    }
  };

  const title = t("recordingConsent.public.revoke.title");

  if (load.kind === "dead") {
    return (
      <ConsentShell title={title}>
        <p role="status" className="mt-3 text-body-md text-error">{t("recordingConsent.public.dead")}</p>
      </ConsentShell>
    );
  }

  if (done) {
    return (
      <ConsentShell title={title}>
        <p role="status" className="mt-3 text-body-md text-onSurface">{t("recordingConsent.public.revoke.done")}</p>
      </ConsentShell>
    );
  }

  return (
    <ConsentShell title={title}>
      {load.kind === "loading" && (
        <p role="status" className="mt-3 text-body-md text-onSurfaceVariant">{t("recordingConsent.public.loading")}</p>
      )}

      {load.kind === "error" && (
        <>
          <p role="alert" className="mt-3 text-body-md text-error">{t("recordingConsent.public.loadFailed")}</p>
          <button
            type="button"
            onClick={() => setAttempt((n) => n + 1)}
            className="mt-4 px-4 py-2.5 bg-primary text-onPrimary rounded-radii-md text-label-lg hover:opacity-90"
          >
            {t("recordingConsent.public.retry")}
          </button>
        </>
      )}

      {load.kind === "ready" && (
        <form onSubmit={submit} className="mt-3 flex flex-col gap-5">
          <p className="text-body-md text-onSurfaceVariant">
            {t("recordingConsent.public.revoke.intro", {
              signer: load.view.signerName,
              date: new Intl.DateTimeFormat(i18n.language, { dateStyle: "long", timeZone: "America/Sao_Paulo" }).format(new Date(load.view.signedAt)),
              provider: load.view.providerName,
            })}
          </p>
          <p className="text-body-md text-onSurface">{t("recordingConsent.public.revoke.effect")}</p>

          <div className="max-h-[24rem] overflow-y-auto rounded-radii-lg border border-outlineVariant bg-surfaceContainerLow p-4 text-body-md text-onSurface">
            <p className="whitespace-pre-wrap">{load.view.text}</p>
          </div>
          <p className="break-all text-body-sm text-onSurfaceVariant">
            {t("recordingConsent.public.revoke.hash", { hash: load.view.termSha256 })}
          </p>

          <div className="flex flex-col gap-1 text-label-md text-onSurfaceVariant">
            <label htmlFor="consent-birth-date">{t("recordingConsent.public.revoke.birthDate")}</label>
            <input
              id="consent-birth-date"
              aria-describedby="consent-birth-date-hint"
              type="date"
              required
              value={birthDate}
              onChange={(e) => setBirthDate(e.target.value)}
              autoComplete="off"
              className={fieldClass}
            />
            <span id="consent-birth-date-hint" className="text-body-sm">{t("recordingConsent.public.revoke.birthDateHint")}</span>
          </div>

          {failure && (
            <p role="alert" className="text-body-md text-error">
              {t(`recordingConsent.public.revoke.errors.${failure}`, { defaultValue: t("recordingConsent.public.revoke.errors.unknown") })}
            </p>
          )}

          <div>
            <button
              type="submit"
              disabled={!birthDate || submitting}
              className="px-5 py-2.5 bg-error text-onError rounded-radii-md text-label-lg hover:opacity-90 disabled:opacity-60"
            >
              {submitting ? t("recordingConsent.public.revoke.submitting") : t("recordingConsent.public.revoke.submit")}
            </button>
          </div>
        </form>
      )}
    </ConsentShell>
  );
}
