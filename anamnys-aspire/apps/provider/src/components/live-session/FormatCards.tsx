import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { Check, FileText } from "lucide-react";
import type { LiveNoteFormat } from "@anamnys/shared/lib/types";

const FORMATS: LiveNoteFormat[] = ["DAP", "SOAP", "Biopsychosocial"];

interface Props {
  value: LiveNoteFormat;
  onChange: (format: LiveNoteFormat) => void;
  disabled?: boolean;
  // Rendered under the options, e.g. the note page's "Reorganizar" button.
  children?: ReactNode;
}

// Note format as selectable cards: each carries its own short explanation, so no tooltip is needed.
// Shared by the live session (choose before recording) and the note page (regenerate).
export default function FormatCards({ value, onChange, disabled, children }: Props) {
  const { t } = useTranslation();
  return (
    <section className="bg-surfaceContainerLowest rounded-radii-xl border border-outlineVariant p-5 shadow-sm">
      <h2 id="live-session-format" className="mb-4 flex items-center gap-2 text-title-md text-onSurface">
        <FileText size={20} className="text-primary" aria-hidden />
        {t("liveSession.format")}
      </h2>
      <div role="radiogroup" aria-labelledby="live-session-format" className="flex flex-col gap-2">
        {FORMATS.map((f) => {
          const selected = f === value;
          return (
            <button
              key={f}
              type="button"
              role="radio"
              aria-checked={selected}
              disabled={disabled}
              onClick={() => onChange(f)}
              className={`flex items-start justify-between gap-3 rounded-radii-lg border p-3 text-left transition-colors disabled:opacity-60 ${
                selected
                  ? "border-primary bg-primary text-onPrimary"
                  : "border-outlineVariant bg-surfaceContainerLow text-onSurface hover:border-primary"
              }`}
            >
              <span>
                <span className="block text-label-lg">{t(`liveSession.formats.${f}`)}</span>
                <span className={`block text-body-sm ${selected ? "text-onPrimary/90" : "text-onSurfaceVariant"}`}>
                  {t(`liveSession.formatHelp.${f}`)}
                </span>
              </span>
              {selected && <Check size={18} aria-hidden className="mt-0.5 shrink-0" />}
            </button>
          );
        })}
      </div>
      {children}
    </section>
  );
}
