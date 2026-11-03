import { useState } from "react";
import { useTranslation } from "react-i18next";
import { ApiError } from "@anamnys/shared/api/client";
import { sharesApi } from "@anamnys/shared/api/sharing";
import type { DisclosureObtainedVia } from "@anamnys/shared/lib/types";
import Checkbox from "@anamnys/shared/ui/Checkbox";
import FormField, { inputClass } from "@/components/patients/FormField";
import Modal from "@/components/patients/Modal";

const VIA: DisclosureObtainedVia[] = ["verbal_session", "signed_term", "in_writing"];

function localToday() {
  const now = new Date();
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${now.getFullYear()}-${pad(now.getMonth() + 1)}-${pad(now.getDate())}`;
}

interface Props {
  documentId: string;
  onClose: () => void;
  onRecorded: (authorization: { id: string; recipientName: string; existing: boolean }) => void;
  onBlocked: () => void;
}

// Step 1 of the share flow: nothing past this point exists without a recorded authorization.
export default function AuthorizationStep({ documentId, onClose, onRecorded, onBlocked }: Props) {
  const { t } = useTranslation();
  const [today] = useState(localToday);
  const [recipientName, setRecipientName] = useState("");
  const [recipientRole, setRecipientRole] = useState("");
  const [purpose, setPurpose] = useState("");
  const [authorizedAt, setAuthorizedAt] = useState(today);
  const [obtainedVia, setObtainedVia] = useState<DisclosureObtainedVia | "">("");
  const [term, setTerm] = useState<File | null>(null);
  const [confirmed, setConfirmed] = useState(false);
  const [saving, setSaving] = useState(false);
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const [formError, setFormError] = useState<string | null>(null);

  const complete =
    recipientName.trim() && recipientRole.trim() && purpose.trim() && authorizedAt && authorizedAt <= today && obtainedVia && confirmed;

  const submit = async () => {
    if (!complete || !obtainedVia) return;
    setSaving(true);
    setErrors({});
    setFormError(null);
    try {
      const name = recipientName.trim();
      const { id, existing } = await sharesApi.authorize(documentId, {
        recipientName: name,
        recipientRole: recipientRole.trim(),
        purpose: purpose.trim(),
        authorizedAt,
        obtainedVia,
        term,
      });
      onRecorded({ id, recipientName: name, existing });
    } catch (e) {
      if (e instanceof ApiError && e.errors) {
        setErrors(e.errors);
      } else {
        const known = e instanceof ApiError && (e.status === 400 || e.status === 409 || e.status === 503);
        setFormError(known ? e.message : t("sharing.authorization.failed"));
        if (e instanceof ApiError && e.status === 409) onBlocked();
      }
    } finally {
      setSaving(false);
    }
  };

  return (
    <Modal
      title={t("sharing.authorization.title")}
      onClose={() => !saving && onClose()}
      footer={
        <>
          <button
            type="button"
            disabled={saving}
            onClick={onClose}
            className="rounded-radii-md px-4 py-2 text-label-lg text-onSurfaceVariant hover:bg-pastelLavender disabled:opacity-50"
          >
            {t("common.cancel")}
          </button>
          <button
            type="button"
            disabled={!complete || saving}
            onClick={() => void submit()}
            className="rounded-radii-md bg-primary px-4 py-2 text-label-lg text-onPrimary disabled:opacity-50"
          >
            {t("sharing.authorization.submit")}
          </button>
        </>
      }
    >
      <div className="flex flex-col gap-4 p-5">
        <p className="text-body-md text-onSurfaceVariant">{t("sharing.authorization.intro")}</p>
        <div className="grid gap-4 sm:grid-cols-2">
          <FormField
            label={t("sharing.authorization.recipientName")}
            value={recipientName}
            maxLength={200}
            autoComplete="off"
            error={errors.recipientName}
            onChange={(e) => setRecipientName(e.target.value)}
          />
          <FormField
            label={t("sharing.authorization.recipientRole")}
            value={recipientRole}
            maxLength={100}
            autoComplete="off"
            error={errors.recipientRole}
            onChange={(e) => setRecipientRole(e.target.value)}
          />
        </div>
        <div className="flex flex-col gap-1">
          <label className="flex flex-col gap-1 text-label-md text-onSurfaceVariant">
            {t("sharing.authorization.purpose")}
            <textarea
              className={`${inputClass} min-h-20 resize-y`}
              value={purpose}
              maxLength={500}
              aria-invalid={errors.purpose ? true : undefined}
              onChange={(e) => setPurpose(e.target.value)}
            />
          </label>
          {errors.purpose && <span className="text-label-md text-error">{errors.purpose[0]}</span>}
        </div>
        <div className="grid gap-4 sm:grid-cols-2">
          <FormField
            label={t("sharing.authorization.authorizedAt")}
            type="date"
            value={authorizedAt}
            max={today}
            error={errors.authorizedAt}
            onChange={(e) => setAuthorizedAt(e.target.value)}
          />
          <div className="flex flex-col gap-1">
            <label className="flex flex-col gap-1 text-label-md text-onSurfaceVariant">
              {t("sharing.authorization.obtainedVia")}
              <select
                className={inputClass}
                value={obtainedVia}
                aria-invalid={errors.obtainedVia ? true : undefined}
                onChange={(e) => setObtainedVia(e.target.value as DisclosureObtainedVia | "")}
              >
                <option value="">{t("sharing.authorization.choose")}</option>
                {VIA.map((via) => (
                  <option key={via} value={via}>
                    {t(`sharing.obtainedVia.${via}`)}
                  </option>
                ))}
              </select>
            </label>
            {errors.obtainedVia && <span className="text-label-md text-error">{errors.obtainedVia[0]}</span>}
          </div>
        </div>
        <label className="flex flex-col gap-1 text-label-md text-onSurfaceVariant">
          {t("sharing.authorization.term")}
          <input
            type="file"
            accept=".pdf,.jpg,.jpeg,.png,application/pdf,image/jpeg,image/png"
            className="text-body-md text-onSurface"
            onChange={(e) => setTerm(e.target.files?.[0] ?? null)}
          />
          <span className="text-body-sm">{t("sharing.authorization.termHint")}</span>
        </label>
        <div className="flex flex-col gap-1">
          <Checkbox checked={confirmed} onToggle={() => setConfirmed((c) => !c)} className="text-body-md text-onSurface">
            {t("sharing.authorization.confirm")}
          </Checkbox>
          {errors.confirmed && <span className="text-label-md text-error">{errors.confirmed[0]}</span>}
        </div>
        {formError && (
          <p role="alert" className="text-body-md text-error">
            {formError}
          </p>
        )}
      </div>
    </Modal>
  );
}
