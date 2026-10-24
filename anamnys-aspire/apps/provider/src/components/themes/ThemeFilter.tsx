import { useMemo } from "react";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { themesApi } from "@anamnys/shared/api/themes";
import { inputClass } from "@/components/patients/FormField";
import { groupTerms } from "./themeGroups";

interface Props {
  value: string;
  onChange: (termId: string) => void;
}

// "Tema" select grouped by parent; retired terms stay listed so their signed notes remain findable.
export default function ThemeFilter({ value, onChange }: Props) {
  const { t } = useTranslation();
  const query = useQuery({ queryKey: ["theme-vocabulary"], queryFn: themesApi.vocabulary, staleTime: 5 * 60_000 });
  const groups = useMemo(() => groupTerms(query.data ?? [], () => true), [query.data]);
  const label = (term: { label: string; status: string }) =>
    term.status === "active" ? term.label : `${term.label} ${t("themes.retired")}`;

  return (
    <label className="flex flex-col gap-1 text-label-md text-onSurfaceVariant sm:max-w-xs">
      {t("themes.filter.label")}
      <select className={inputClass} value={value} onChange={(e) => onChange(e.target.value)}>
        <option value="">{t("themes.filter.all")}</option>
        {groups.map((g) =>
          g.children.length === 0 ? (
            <option key={g.parent.id} value={g.parent.id}>
              {label(g.parent)}
            </option>
          ) : (
            <optgroup key={g.parent.id} label={g.parent.label}>
              <option value={g.parent.id}>{label(g.parent)}</option>
              {g.children.map((c) => (
                <option key={c.id} value={c.id}>
                  {label(c)}
                </option>
              ))}
            </optgroup>
          ),
        )}
      </select>
    </label>
  );
}
