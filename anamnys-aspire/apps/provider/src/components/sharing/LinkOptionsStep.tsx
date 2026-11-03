import { useState } from "react";
import { useTranslation } from "react-i18next";
import { ApiError } from "@anamnys/shared/api/client";
import { sharesApi } from "@anamnys/shared/api/sharing";
import type { CreatedShareLink } from "@anamnys/shared/lib/types";
import Checkbox from "@anamnys/shared/ui/Checkbox";
import FormField from "@/components/patients/FormField";
import Modal from "@/components/patients/Modal";

const DEFAULT_DAYS = 7;
const MAX_DAYS = 30;

const wholeNumber = (value: string) => (/^\d+$/.test(value.trim()) ? Number(value) : NaN);

interface Props {
  documentId: string;
  authorizationId: string;
  recipientName: string;
  /** The form matched an authorization already on record instead of adding one. */
  reusedAuthorization: boolean;
  onClose: () => void;
  onCreated: (link: CreatedShareLink) => void;
  onBlocked: () => void;
}

// Step 2 of the share flow. Only reachable with the id of a recorded authorization.
export default function LinkOptionsStep({
  documentId,
  authorizationId,
  recipientName,
  reusedAuthorization,
  onClose,
  onCreated,
  onBlocked,
}: Props) {
  const { t } = useTranslation();
  const [days, setDays] = useState(String(DEFAULT_DAYS));
  const [viewCap, setViewCap] = useState("");
  const [downloadEnabled, setDownloadEnabled] = useState(false);
  const [creating, setCreating] = useState(false);
  const [errors, setErrors] = useState<Record<string, string[]>>({});
  const [formError, setFormError] = useState<string | null>(null);

  const parsedDays = wholeNumber(days);
  const parsedCap = viewCap.trim() ? wholeNumber(viewCap) : null;
  const daysValid = parsedDays >= 1 && parsedDays <= MAX_DAYS;
  const capValid = parsedCap === null || parsedCap >= 1;

  const submit = async () => {
    if (!daysValid || !capValid) return;
    setCreating(true);
    setErrors({});
    setFormError(null);
    try {
      onCreated(await sharesApi.createLink(documentId, { authorizationId, expiresInDays: parsedDays, viewCap: parsedCap, downloadEnabled }));
    } catch (e) {
      if (e instanceof ApiError && e.errors) {
        setErrors(e.errors);
      } else {
        // 429 is `busy`: no link was made. Own copy, since a rate-limit 429 has no body to show.
        const busy = e instanceof ApiError && e.status === 429;
        setFormError(busy ? t("sharing.link.busy") : e instanceof ApiError && e.status === 409 ? e.message : t("sharing.link.failed"));
        if (e instanceof ApiError && e.status === 409) onBlocked();
      }
    } finally {
      setCreating(false);
    }
  };

  return (
    <Modal
      title={t("sharing.link.title")}
      // A request in flight may already have made the link: closing now would lose its password.
      onClose={() => !creating && onClose()}
      footer={
        <>
          <button
            type="button"
            disabled={creating}
            onClick={onClose}
            className="rounded-radii-md px-4 py-2 text-label-lg text-onSurfaceVariant hover:bg-pastelLavender disabled:opacity-50"
          >
            {t("common.cancel")}
          </button>
          <button
            type="button"
            disabled={!daysValid || !capValid || creating}
            onClick={() => void submit()}
            className="rounded-radii-md bg-primary px-4 py-2 text-label-lg text-onPrimary disabled:opacity-50"
          >
            {t("sharing.link.submit")}
          </button>
        </>
      }
    >
      <div className="flex flex-col gap-4 p-5">
        <p className="text-body-md text-onSurfaceVariant">
          {t("sharing.link.recipient")} <span className="text-onSurface">{recipientName}</span>
        </p>
        {reusedAuthorization && <p className="rounded-radii-lg bg-surfaceContainerLow p-3 text-body-sm text-onSurface">{t("sharing.link.reused")}</p>}
        <div className="grid gap-4 sm:grid-cols-2">
          <FormField
            label={t("sharing.link.expiresInDays")}
            type="number"
            inputMode="numeric"
            min={1}
            max={MAX_DAYS}
            value={days}
            error={errors.expiresInDays ?? (daysValid ? undefined : [t("sharing.link.expiresInvalid", { max: MAX_DAYS })])}
            onChange={(e) => setDays(e.target.value)}
          />
          <FormField
            label={t("sharing.link.viewCap")}
            type="number"
            inputMode="numeric"
            min={1}
            value={viewCap}
            placeholder={t("sharing.link.viewCapNone")}
            error={errors.viewCap ?? (capValid ? undefined : [t("sharing.link.viewCapInvalid")])}
            onChange={(e) => setViewCap(e.target.value)}
          />
        </div>
        <Checkbox checked={downloadEnabled} onToggle={() => setDownloadEnabled((d) => !d)} className="text-body-md text-onSurface">
          {t("sharing.link.download")}
        </Checkbox>
        <p className="text-body-sm text-onSurfaceVariant">{t("sharing.link.watermarkHint")}</p>
        {formError && (
          <p role="alert" className="text-body-md text-error">
            {formError}
          </p>
        )}
      </div>
    </Modal>
  );
}
