import { useMemo, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { themesApi } from "@anamnys/shared/api/themes";
import type { ThemeTerm } from "@anamnys/shared/lib/types";
import { inputClass } from "@/components/patients/FormField";
import { groupTerms } from "./themeGroups";

interface Props {
  taggedTermIds: Set<string>;
  disabled?: boolean;
  onPick: (termId: string) => void;
}

const normalize = (s: string) => s.normalize("NFD").replace(/\p{Diacritic}/gu, "").toLowerCase();

// Searchable list of active terms grouped by parent. Terms already on the note are left out.
export default function ThemePicker({ taggedTermIds, disabled, onPick }: Props) {
  const { t } = useTranslation();
  const [text, setText] = useState("");
  const query = useQuery({ queryKey: ["theme-vocabulary"], queryFn: themesApi.vocabulary, staleTime: 5 * 60_000 });
  const listed = useMemo(() => {
    const needle = normalize(text.trim());
    return (term: ThemeTerm) =>
      term.status === "active" && !taggedTermIds.has(term.id) && normalize(term.label).includes(needle);
  }, [taggedTermIds, text]);
  const groups = useMemo(() => groupTerms(query.data ?? [], listed), [query.data, listed]);

  const option =
    "w-full px-3 py-2 text-left text-body-md text-onSurface hover:bg-pastelLavender disabled:opacity-60";

  return (
    <div className="flex flex-col gap-1 rounded-radii-md border border-outlineVariant p-2">
      <input
        className={inputClass}
        value={text}
        onChange={(e) => setText(e.target.value)}
        placeholder={t("themes.pickerSearch")}
        aria-label={t("themes.pickerSearch")}
        autoFocus
      />
      <div className="max-h-64 overflow-y-auto">
        {query.isError && <p className="px-3 py-2 text-body-md text-error">{t("themes.errors.generic")}</p>}
        {query.isSuccess && groups.length === 0 && (
          <p className="px-3 py-2 text-body-md text-onSurfaceVariant">{t("themes.pickerNone")}</p>
        )}
        {groups.map((g) => (
          <div key={g.parent.id} role="group" aria-label={g.parent.label}>
            {g.children.length > 0 && (
              <p className="px-3 pt-2 text-label-md uppercase text-onSurfaceVariant">{g.parent.label}</p>
            )}
            {listed(g.parent) && (
              <button type="button" disabled={disabled} onClick={() => onPick(g.parent.id)} className={option}>
                {g.children.length > 0 ? t("themes.pickerGeneral", { label: g.parent.label }) : g.parent.label}
              </button>
            )}
            {g.children.map((c) => (
              <button
                key={c.id}
                type="button"
                disabled={disabled}
                onClick={() => onPick(c.id)}
                className={`${option} pl-6`}
              >
                {c.label}
              </button>
            ))}
          </div>
        ))}
      </div>
    </div>
  );
}
