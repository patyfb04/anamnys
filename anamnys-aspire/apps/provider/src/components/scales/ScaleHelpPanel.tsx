import { useTranslation } from "react-i18next";
import Modal from "@/components/patients/Modal";

const ITEMS = ["available", "needsOptIn", "unavailable", "stored", "mistake"] as const;

export default function ScaleHelpPanel({ onClose }: { onClose: () => void }) {
  const { t } = useTranslation();
  return (
    <Modal title={t("scales.help")} onClose={onClose}>
      <ul className="flex list-disc flex-col gap-3 px-9 py-5 text-body-md text-onSurface">
        {ITEMS.map((key) => (
          <li key={key}>{t(`scales.helpItems.${key}`)}</li>
        ))}
      </ul>
    </Modal>
  );
}
