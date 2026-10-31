import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { themesApi } from "@anamnys/shared/api/themes";
import { inputClass } from "@/components/patients/FormField";
import { groupTerms } from "./themeGroups";

interface Props {
  label: string;
  value: string | null;
  /** Label of the term already linked, shown when it is no longer active (so the link is not dropped silently). */
  currentLabel?: string | null;
  onChange: (termId: string | null) => void;
  error?: string;
  className?: string;
}

// Optional select of active vocabulary terms; children are indented under their parent.
export default function TermSelect({ label, value, currentLabel, onChange, error, className }: Props) {
  const { t } = useTranslation();
  const query = useQuery({ queryKey: ["theme-vocabulary"], queryFn: themesApi.vocabulary, staleTime: 5 * 60_000 });
  const groups = useMemo(() => groupTerms(query.data ?? [], (term) => term.status === "active"), [query.data]);
  const activeIds = useMemo(() => new Set((query.data ?? []).filter((x) => x.status === "active").map((x) => x.id)), [query.data]);
  // Wait for the vocabulary before deciding that the linked term is retired.
  const retired = query.isSuccess && value && !activeIds.has(value);

  return (
    <label className={`flex flex-col gap-1 text-label-md text-onSurfaceVariant ${className ?? ""}`}>
      {label}
      <select
        className={inputClass}
        value={value ?? ""}
        onChange={(e) => onChange(e.target.value || null)}
        aria-invalid={error ? true : undefined}
      >
        <option value="">{t("planAdherence.noTermOption")}</option>
        {retired && <option value={value}>{currentLabel ?? ""}</option>}
        {groups.map((g) => (
          <optgroup key={g.parent.id} label={g.parent.label}>
            {g.parent.status === "active" && <option value={g.parent.id}>{g.parent.label}</option>}
            {g.children.map((c) => (
              <option key={c.id} value={c.id}>
                {"   "}
                {c.label}
              </option>
            ))}
          </optgroup>
        ))}
      </select>
      {error && <span className="text-label-md text-error">{error}</span>}
    </label>
  );
}
