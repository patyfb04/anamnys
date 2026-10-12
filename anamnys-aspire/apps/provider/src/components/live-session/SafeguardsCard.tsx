import { useTranslation } from "react-i18next";
import { Cpu, ShieldCheck, Sparkles, Trash2 } from "lucide-react";

// Only guarantees that hold today (design/specs/2026-10-10 and 2026-10-12): nothing aspirational.
export default function SafeguardsCard() {
  const { t } = useTranslation();
  const items = [
    { key: "memory", Icon: Trash2 },
    { key: "local", Icon: Cpu },
    { key: "organise", Icon: Sparkles },
  ] as const;
  return (
    <section className="bg-surfaceContainerLowest rounded-radii-xl border border-outlineVariant p-5 shadow-sm">
      <h2 className="mb-4 flex items-center gap-2 text-title-md text-onSurface">
        <ShieldCheck size={20} className="text-primary" aria-hidden />
        {t("liveSession.safeguardsTitle")}
      </h2>
      <ul className="flex flex-col gap-3">
        {items.map(({ key, Icon }) => (
          <li key={key} className="flex items-start gap-3 text-body-sm text-onSurfaceVariant">
            <Icon size={16} className="mt-0.5 shrink-0 text-primary" aria-hidden />
            {t(`liveSession.safeguards.${key}`)}
          </li>
        ))}
      </ul>
    </section>
  );
}
