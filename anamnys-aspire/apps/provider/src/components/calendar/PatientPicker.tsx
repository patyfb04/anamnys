import { useEffect, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { patientsApi } from "@anamnys/shared/api/patients";
import { inputClass } from "@/components/patients/FormField";

interface Props {
  value: { id: string; name: string } | null;
  onChange: (value: { id: string; name: string } | null) => void;
  error?: string[];
}

const MIN_CHARS = 2;

// Searches the provider's active patients by name. Uses the POST search endpoint so the
// typed name never appears in a URL.
export default function PatientPicker({ value, onChange, error }: Props) {
  const { t } = useTranslation();
  const [text, setText] = useState("");
  const [debounced, setDebounced] = useState("");
  useEffect(() => {
    const timer = setTimeout(() => setDebounced(text.trim()), 300);
    return () => clearTimeout(timer);
  }, [text]);

  const query = useQuery({
    queryKey: ["calendar-patient-search", debounced],
    queryFn: () => patientsApi.search({ search: debounced, sortBy: "name", sortDir: "asc", page: 1, pageSize: 8 }),
    enabled: !value && debounced.length >= MIN_CHARS,
  });

  if (value) {
    return (
      <div className="flex items-center justify-between rounded-radii-md border border-outlineVariant px-3 py-2">
        <span className="text-body-md text-onSurface">{value.name}</span>
        <button type="button" onClick={() => onChange(null)} className="text-label-md text-primary">
          {t("calendar.form.patientChange")}
        </button>
      </div>
    );
  }

  const items = query.data?.items ?? [];
  return (
    <div className="flex flex-col gap-1">
      <input
        className={inputClass}
        value={text}
        onChange={(e) => setText(e.target.value)}
        placeholder={t("calendar.form.patientSearch")}
        aria-invalid={error ? true : undefined}
        autoFocus
      />
      {debounced.length >= MIN_CHARS && query.isSuccess && (
        <ul className="max-h-56 overflow-y-auto rounded-radii-md border border-outlineVariant">
          {items.length === 0 && <li className="px-3 py-2 text-body-md text-onSurfaceVariant">{t("calendar.form.patientNone")}</li>}
          {items.map((p) => (
            <li key={p.id}>
              <button
                type="button"
                onClick={() => onChange({ id: p.id, name: `${p.firstName} ${p.lastName}` })}
                className="w-full px-3 py-2 text-left text-body-md text-onSurface hover:bg-surfaceContainer"
              >
                {p.firstName} {p.lastName}
              </button>
            </li>
          ))}
        </ul>
      )}
      {error && <span className="text-label-md text-error">{error[0]}</span>}
    </div>
  );
}
