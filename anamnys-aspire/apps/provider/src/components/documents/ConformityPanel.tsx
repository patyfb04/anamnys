import { useState } from "react";
import { Link } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { Check, ClipboardCheck, PenLine, X } from "lucide-react";
import type { DocumentDetail, DocumentKindInfo } from "@anamnys/shared/lib/types";
import DetailCard from "@/components/patients/detail/DetailCard";
import ConfirmDialog from "@/components/patients/ConfirmDialog";

interface Props {
  kind: DocumentKindInfo;
  detail: DocumentDetail;
  confirmations: string[];
  dirty: boolean;
  signing: boolean;
  saving: boolean;
  error: string | null;
  errorCode: string | null;
  onToggle: (code: string, checked: boolean) => void;
  onSign: (acknowledgeMissing: boolean) => void;
}

// The CFP 006/2019 check: one line per element of the ruleset, the confirmations for the
// prohibitions the system cannot check, and the sign action. The result comes from the server
// for the saved text, so unsaved edits ask for a save first.
export default function ConformityPanel({ kind, detail, confirmations, dirty, signing, saving, error, errorCode, onToggle, onSign }: Props) {
  const { t } = useTranslation();
  const [confirming, setConfirming] = useState(false);
  const draft = detail.status === "draft";
  const { check } = detail;
  const missing = new Set(check.missingElements);
  const missingConfirmations = new Set(check.missingConfirmations);
  const asked = kind.prohibitions.filter((p) => !p.byConstruction);
  const blocking = detail.mode === "blocking";
  const canSign = draft && !dirty && check.confirmationsComplete && (!blocking || check.elementsComplete);

  return (
    <DetailCard title={t("documents.panelTitle")} icon={<ClipboardCheck size={20} />}>
      <p className="mb-3 text-body-sm text-onSurfaceVariant">
        {t(blocking ? "documents.modeBlocking" : "documents.modeAdvisory")}
      </p>

      {draft && dirty && <p className="mb-3 text-body-sm text-onSurfaceVariant">{t("documents.unsavedHint")}</p>}

      <ul className="flex flex-col gap-2">
        {kind.elements.map((el) => {
          const ok = !missing.has(el.code);
          return (
            <li key={el.code} className="flex items-start gap-2 text-body-md">
              {ok ? (
                <Check size={18} role="img" className="mt-0.5 shrink-0 text-primary" aria-label={t("documents.present")} />
              ) : (
                <X size={18} role="img" className="mt-0.5 shrink-0 text-error" aria-label={t("documents.absent")} />
              )}
              <span className={ok ? "text-onSurface" : "text-error"}>
                {el.label}
                {el.origin !== "provider" && (
                  <span className="block text-body-sm text-onSurfaceVariant">{t(`documents.origin.${el.origin}`)}</span>
                )}
              </span>
            </li>
          );
        })}
      </ul>

      {asked.length > 0 && (
        <fieldset className="mt-5 flex flex-col gap-3 border-t border-outlineVariant pt-4">
          <legend className="mb-1 text-label-lg text-onSurface">{t("documents.confirmationsTitle")}</legend>
          {asked.map((p) => {
            const text = p.confirmationText ?? t("documents.confirmFallback");
            return (
              <label key={p.code} className="flex items-start gap-2 text-body-md text-onSurface">
                <input
                  type="checkbox"
                  className="mt-1"
                  checked={confirmations.includes(p.code)}
                  disabled={!draft || saving}
                  onChange={(e) => onToggle(p.code, e.target.checked)}
                />
                <span className={draft && !dirty && missingConfirmations.has(p.code) ? "text-error" : undefined}>{text}</span>
              </label>
            );
          })}
        </fieldset>
      )}

      {draft && (
        <div className="mt-5">
          <button
            type="button"
            disabled={!canSign || signing}
            onClick={() => setConfirming(true)}
            className="inline-flex w-full items-center justify-center gap-2 rounded-radii-lg bg-primary px-4 py-3 text-label-lg text-onPrimary disabled:opacity-60"
          >
            <PenLine size={16} aria-hidden />
            {t("documents.sign")}
          </button>
          {error && (
            <p role="alert" className="mt-2 text-body-sm text-error">
              {error}
              {(errorCode === "missing_crp" || errorCode === "missing_city") && (
                <>
                  {" "}
                  <Link to="/account/profile" className="text-primary hover:underline">
                    {t("documents.goToProfile")}
                  </Link>
                </>
              )}
            </p>
          )}
        </div>
      )}

      <ConfirmDialog
        open={confirming}
        onOpenChange={setConfirming}
        title={check.elementsComplete ? t("documents.signConfirmTitle") : t("documents.signMissingTitle")}
        description={check.elementsComplete ? t("documents.signConfirmBody") : t("documents.signMissingBody")}
        confirmLabel={t("documents.sign")}
        pending={signing}
        onConfirm={() => {
          setConfirming(false);
          onSign(!check.elementsComplete);
        }}
      />
    </DetailCard>
  );
}
