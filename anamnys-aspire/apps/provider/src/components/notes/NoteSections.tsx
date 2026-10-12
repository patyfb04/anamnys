import { useTranslation } from "react-i18next";
import type { NoteSectionView } from "@anamnys/shared/lib/types";

export default function NoteSections({ sections }: { sections: NoteSectionView[] }) {
  const { t } = useTranslation();
  const ordered = [...sections].sort((a, b) => a.position - b.position);
  const allUnedited = ordered.every((s) => !s.editedAt);

  return (
    <div className="flex flex-col gap-4">
      {allUnedited && (
        <span className="self-start rounded-radii-md bg-surfaceVariant px-2 py-1 text-label-md text-onSurfaceVariant">
          {t("noteView.aiDraft")}
        </span>
      )}
      {ordered.map((s) => (
        <section key={s.key}>
          <h3 className="mb-1 text-title-md">{t(`noteView.sections.${s.key}`)}</h3>
          {s.content ? (
            <p className="whitespace-pre-wrap text-body-md">{s.content}</p>
          ) : (
            <p className="text-body-md text-onSurfaceVariant">{t("noteView.emptySection")}</p>
          )}
        </section>
      ))}
    </div>
  );
}
