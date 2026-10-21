import { useTranslation } from "react-i18next";
import Modal from "@/components/patients/Modal";

const ITEMS = ["form", "system", "panel", "confirmations", "mode", "correction", "laudo", "parecer", "multiprofissional"] as const;

export default function DocumentHelpPanel({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation();
  return (
    <Modal title={t("documents.help")} onClose={onClose}>
      <ul className="flex list-disc flex-col gap-3 px-9 py-5 text-body-md text-onSurface">
        {ITEMS.map((key) => (
          <li key={key}>{t(`documents.helpItems.${key}`)}</li>
        ))}
      </ul>
    </Modal>
  );
}
