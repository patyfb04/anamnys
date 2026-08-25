import { useTranslation } from "react-i18next";
import { SUPPORTED_LANGUAGES } from "@/lib/types";

interface Props {
  value?: string; // undefined = auto-detect
  onChange: (code: string | undefined) => void;
  className?: string;
}

// Chip selector for a Whisper transcription language, plus an "Auto-detect" option.
export default function LanguagePicker({ value, onChange, className }: Props) {
  const { t } = useTranslation();
  return (
    <div className={`flex flex-wrap gap-2 ${className ?? ""}`}>
      <button
        type="button"
        onClick={() => onChange(undefined)}
        className={`rounded-radii-full border px-3.5 py-2 text-label-lg ${
          !value ? "border-primary bg-primaryFixed text-onPrimaryFixedVariant" : "border-outlineVariant text-onSurfaceVariant"
        }`}
      >
        {t("common.autoDetect")}
      </button>
      {SUPPORTED_LANGUAGES.map((lang) => (
        <button
          type="button"
          key={lang.code}
          onClick={() => onChange(lang.code)}
          className={`rounded-radii-full border px-3.5 py-2 text-label-lg ${
            value === lang.code ? "border-primary bg-primaryFixed text-onPrimaryFixedVariant" : "border-outlineVariant text-onSurfaceVariant"
          }`}
        >
          {lang.label}
        </button>
      ))}
    </div>
  );
}
