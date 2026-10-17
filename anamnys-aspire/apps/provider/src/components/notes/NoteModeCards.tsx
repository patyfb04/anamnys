import { useTranslation } from "react-i18next";
import { Check, PenLine, Sparkles } from "lucide-react";
import type { NoteMode } from "@anamnys/shared/lib/types";

interface Props {
  value: NoteMode;
  onChange: (mode: NoteMode) => void;
}

const MODES: { mode: NoteMode; Icon: typeof Sparkles; label: string; hint: string }[] = [
  { mode: "ai", Icon: Sparkles, label: "notes.create.modeAi", hint: "notes.create.modeAiHint" },
  { mode: "manual", Icon: PenLine, label: "notes.create.modeManual", hint: "notes.create.modeManualHint" },
];

// Two selectable cards: organise free text with the AI, or start from empty sections.
export default function NoteModeCards({ value, onChange }: Props) {
  const { t } = useTranslation();
  return (
    <div role="radiogroup" aria-label={t("notes.create.mode")} className="grid gap-3 sm:grid-cols-2">
      {MODES.map(({ mode, Icon, label, hint }) => {
        const selected = value === mode;
        return (
          <button
            key={mode}
            type="button"
            role="radio"
            aria-checked={selected}
            onClick={() => onChange(mode)}
            className={`flex items-start gap-3 rounded-radii-lg border p-4 text-left ${
              selected ? "border-primary bg-surfaceContainerLow" : "border-outlineVariant"
            }`}
          >
            <Icon size={20} className="text-primary shrink-0" aria-hidden />
            <span className="flex-1">
              <span className="block text-label-lg text-onSurface">{t(label)}</span>
              <span className="block text-body-sm text-onSurfaceVariant">{t(hint)}</span>
            </span>
            {selected && <Check size={18} className="text-primary" aria-hidden />}
          </button>
        );
      })}
    </div>
  );
}
