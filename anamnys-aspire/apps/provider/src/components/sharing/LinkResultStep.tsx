import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Copy, TriangleAlert } from "lucide-react";
import type { CreatedShareLink } from "@anamnys/shared/lib/types";
import { toast } from "@anamnys/shared/ui/Toaster";
import { inputClass } from "@/components/patients/FormField";
import Modal from "@/components/patients/Modal";

function CopyField({ label, value, copiedMessage }: { label: string; value: string; copiedMessage: string }) {
  const { t } = useTranslation();
  const copy = async () => {
    try {
      await navigator.clipboard.writeText(value);
      toast.success(copiedMessage);
    } catch {
      toast.error(t("sharing.result.copyFailed"));
    }
  };
  return (
    <div className="flex flex-col gap-1">
      <span className="text-label-md text-onSurfaceVariant">{label}</span>
      <div className="flex items-center gap-2">
        <input readOnly aria-label={label} value={value} onFocus={(e) => e.target.select()} className={`${inputClass} font-mono`} />
        <button
          type="button"
          onClick={() => void copy()}
          className="inline-flex shrink-0 items-center gap-1 rounded-radii-md px-3 py-2 text-label-lg text-primary hover:bg-pastelLavender"
        >
          <Copy size={16} aria-hidden />
          {t("sharing.result.copy")}
        </button>
      </div>
    </div>
  );
}

// Step 3 of the share flow: the only time the link and the password can be read. They live in
// component state and go when it closes, so a stray Esc or backdrop click asks first.
export default function LinkResultStep({ link, onClose }: { link: CreatedShareLink; onClose: () => void }) {
  const { t } = useTranslation();
  const [warned, setWarned] = useState(false);

  return (
    <Modal
      title={t("sharing.result.title")}
      onClose={() => (warned ? onClose() : setWarned(true))}
      footer={
        <button type="button" onClick={onClose} className="rounded-radii-md bg-primary px-4 py-2 text-label-lg text-onPrimary">
          {t("sharing.result.done")}
        </button>
      }
    >
      <div className="flex flex-col gap-4 p-5">
        <CopyField label={t("sharing.result.link")} value={link.url} copiedMessage={t("sharing.result.linkCopied")} />
        <CopyField label={t("sharing.result.password")} value={link.password} copiedMessage={t("sharing.result.passwordCopied")} />
        <p className="text-body-sm text-onSurfaceVariant">
          {t("sharing.result.expires", { date: new Date(link.expiresAt).toLocaleString("pt-BR") })}
        </p>
        <div className="flex gap-2 rounded-radii-lg bg-surfaceContainerLow p-3 text-body-md text-onSurface">
          <TriangleAlert size={18} className="mt-0.5 shrink-0 text-error" aria-hidden />
          <div className="flex flex-col gap-1">
            <p>{t("sharing.result.channels")}</p>
            <p>{t("sharing.result.once")}</p>
          </div>
        </div>
        {warned && (
          <p role="alert" className="text-body-md text-error">
            {t("sharing.result.closeWarning")}
          </p>
        )}
      </div>
    </Modal>
  );
}
