import { useEffect, useState, type FormEvent } from "react";
import { createFileRoute } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { publicConsentApi, type PublicConsentView } from "@anamnys/shared/api/publicConsent";
import { ApiError } from "@anamnys/shared/api/client";
import Checkbox from "@anamnys/shared/ui/Checkbox";
import ConsentShell from "@/components/consent/ConsentShell";
import { failureCode, fieldClass, isDeadLink } from "@/components/consent/consentHelpers";

// Public page outside the authenticated `_app` layout: the e-mailed token is the only credential. Plain state and
// direct calls on purpose (no query cache for the token). The date of birth is only typed here and sent once; the
// server never returns it and this page never shows or logs it. See
// design/specs/2026-11-15-recording-consent-design.md §4.
export const Route = createFileRoute("/consentimento/$token")({ component: ConsentSignPage });

type Load = { kind: "loading" } | { kind: "ready"; view: PublicConsentView } | { kind: "dead" } | { kind: "error" };

function ConsentSignPage() {
  const { token } = Route.useParams();
  const { t } = useTranslation();
  const [load, setLoad] = useState<Load>({ kind: "loading" });
  const [attempt, setAttempt] = useState(0);
  const [birthDate, setBirthDate] = useState("");
  const [signerName, setSignerName] = useState("");
  const [relationship, setRelationship] = useState("");
  const [accepted, setAccepted] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [failure, setFailure] = useState<string | null>(null);
  const [signed, setSigned] = useState(false);

  useEffect(() => {
    let active = true;
    setLoad({ kind: "loading" });
    publicConsentApi
      .view(token)
      .then((view) => active && setLoad({ kind: "ready", view }))
      .catch((e) => active && setLoad(isDeadLink(e) ? { kind: "dead" } : { kind: "error" }));
    return () => {
      active = false;
    };
  }, [token, attempt]);

  const guardian = load.kind === "ready" && load.view.guardianExpected;
  const complete = !!birthDate && signerName.trim().length > 0 && (!guardian || relationship.trim().length > 0) && accepted;

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    if (!complete || submitting) return;
    setSubmitting(true);
    setFailure(null);
    try {
      await publicConsentApi.sign(token, {
        birthDate,
        signerName: signerName.trim(),
        relationship: guardian ? relationship.trim() : undefined,
        accepted,
      });
      setBirthDate("");
      setSigned(true);
    } catch (err) {
      if (isDeadLink(err)) setLoad({ kind: "dead" });
      else {
        // 400 invalid: the server lists the fields; one friendly line is enough here.
        const field = err instanceof ApiError ? singleLineField(err.body) : undefined;
        setFailure(field ?? failureCode(err));
      }
    } finally {
      setSubmitting(false);
    }
  };

  const title = t("recordingConsent.public.sign.title");

  if (load.kind === "dead") {
    return (
      <ConsentShell title={title}>
        <p role="status" className="mt-3 text-body-md text-error">{t("recordingConsent.public.dead")}</p>
      </ConsentShell>
    );
  }

  if (signed) {
    return (
      <ConsentShell title={title}>
        <p role="status" className="mt-3 text-body-md text-onSurface">{t("recordingConsent.public.sign.signed")}</p>
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
            {t("recordingConsent.public.sign.intro", { provider: load.view.providerName })}
          </p>

          <section aria-labelledby="term-heading">
            <h2 id="term-heading" className="text-title-sm text-onSurface">{t("recordingConsent.public.sign.termHeading")}</h2>
            <div className="mt-2 max-h-[28rem] overflow-y-auto rounded-radii-lg border border-outlineVariant bg-surfaceContainerLow p-4 text-body-md text-onSurface">
              <p className="whitespace-pre-wrap">{load.view.text}</p>
            </div>
          </section>

          <section className="flex flex-col gap-4" aria-labelledby="identity-heading">
            <h2 id="identity-heading" className="text-title-sm text-onSurface">{t("recordingConsent.public.sign.identityHeading")}</h2>
            {guardian && <p className="text-body-md text-onSurfaceVariant">{t("recordingConsent.public.sign.guardianNote")}</p>}

            <div className="flex flex-col gap-1 text-label-md text-onSurfaceVariant">
              <label htmlFor="consent-birth-date">{t("recordingConsent.public.sign.birthDate")}</label>
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
              <span id="consent-birth-date-hint" className="text-body-sm">{t("recordingConsent.public.sign.birthDateHint")}</span>
            </div>

            <div className="flex flex-col gap-1 text-label-md text-onSurfaceVariant">
              <label htmlFor="consent-signer-name">{t("recordingConsent.public.sign.signerName")}</label>
              <input
                id="consent-signer-name"
                aria-describedby="consent-signer-name-hint"
                type="text"
                required
                maxLength={200}
                value={signerName}
                onChange={(e) => setSignerName(e.target.value)}
                autoComplete="name"
                className={fieldClass}
              />
              <span id="consent-signer-name-hint" className="text-body-sm">{t("recordingConsent.public.sign.signerNameHint")}</span>
            </div>

            {guardian && (
              <div className="flex flex-col gap-1 text-label-md text-onSurfaceVariant">
                <label htmlFor="consent-relationship">{t("recordingConsent.public.sign.relationship")}</label>
                <input
                  id="consent-relationship"
                  aria-describedby="consent-relationship-hint"
                  type="text"
                  required
                  maxLength={100}
                  value={relationship}
                  onChange={(e) => setRelationship(e.target.value)}
                  className={fieldClass}
                />
                <span id="consent-relationship-hint" className="text-body-sm">{t("recordingConsent.public.sign.relationshipHint")}</span>
              </div>
            )}

            <Checkbox checked={accepted} onToggle={() => setAccepted((v) => !v)}>
              <span className="text-body-md text-onSurface">{t("recordingConsent.public.sign.accept")}</span>
            </Checkbox>
          </section>

          {failure && (
            <p role="alert" className="text-body-md text-error">
              {t(`recordingConsent.public.sign.errors.${failure}`, { defaultValue: t("recordingConsent.public.sign.errors.unknown") })}
            </p>
          )}

          <div>
            <button
              type="submit"
              disabled={!complete || submitting}
              className="px-5 py-2.5 bg-primary text-onPrimary rounded-radii-md text-label-lg hover:opacity-90 disabled:opacity-60"
            >
              {submitting ? t("recordingConsent.public.sign.submitting") : t("recordingConsent.public.sign.submit")}
            </button>
          </div>
        </form>
      )}
    </ConsentShell>
  );
}

// The 400 "invalid" body lists field codes; only the line-break ones have their own message.
function singleLineField(body: unknown): string | undefined {
  const fields = (body as { fields?: unknown } | undefined)?.fields;
  return Array.isArray(fields) ? fields.find((v): v is string => typeof v === "string" && v.endsWith("_single_line")) : undefined;
}
