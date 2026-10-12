import { useTranslation } from "react-i18next";
import type { NoteSectionView } from "@anamnys/shared/lib/types";

// The structured note: one tonal block per section, in format order. Empty sections stay visible
// and say so — the AI never fills them in.
export default function NoteSections({ sections }: { sections: NoteSectionView[] }) {
  const { t } = useTranslation();
  const ordered = [...sections].sort((a, b) => a.position - b.position);

  return (
    <div className="flex flex-col gap-3">
      {ordered.map((s) => (
        <section key={s.key} className="rounded-radii-lg bg-surfaceContainerLow p-4">
          <h3 className="mb-1 text-label-lg uppercase tracking-wide text-primary">{t(`noteView.sections.${s.key}`)}</h3>
          {s.content ? (
            <p className="whitespace-pre-wrap text-body-md text-onSurface">{s.content}</p>
          ) : (
            <p className="text-body-md italic text-onSurfaceVariant">{t("noteView.emptySection")}</p>
          )}
        </section>
      ))}
    </div>
  );
}
