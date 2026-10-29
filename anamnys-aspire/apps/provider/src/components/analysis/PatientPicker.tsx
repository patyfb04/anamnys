import { useEffect, useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { patientsApi } from "@anamnys/shared/api/patients";
import { inputClass } from "@/components/patients/FormField";

const SEARCH_DEBOUNCE_MS = 300;

// Patient search for the analysis page. The name typed here is PHI: it stays in component state and
// goes to the server in a POST body; only the chosen patient id reaches the URL.
export default function PatientPicker({ onPick }: { onPick: (patientId: string) => void }) {
  const { t } = useTranslation();
  const [text, setText] = useState("");
  const [term, setTerm] = useState("");
  useEffect(() => {
    const id = setTimeout(() => setTerm(text.trim()), SEARCH_DEBOUNCE_MS);
    return () => clearTimeout(id);
  }, [text]);
  const query = useQuery({
    queryKey: ["analysis-patient-search", term],
    queryFn: () => patientsApi.search({ search: term || undefined, sortBy: "name", sortDir: "asc", page: 1, pageSize: 100 }),
  });

  return (
    <div className="flex w-full flex-col gap-3">
      <label className="flex flex-col gap-1 text-label-md text-onSurfaceVariant">
        {t("analysis.searchLabel")}
        <input
          type="search"
          className={inputClass}
          value={text}
          placeholder={t("analysis.searchPlaceholder")}
          onChange={(e) => setText(e.target.value)}
        />
      </label>
      {query.isError ? (
        <p role="alert" className="text-body-md text-error">
          {t("analysis.searchFailed")}
        </p>
      ) : query.isPending ? (
        <p className="text-body-md text-onSurfaceVariant">{t("analysis.searching")}</p>
      ) : query.data.items.length === 0 ? (
        <p className="text-body-md text-onSurfaceVariant">{t("analysis.noPatients")}</p>
      ) : (
        <ul className="grid grid-cols-1 gap-2 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4">
          {query.data.items.map((p) => (
            <li key={p.id}>
              <button
                type="button"
                onClick={() => onPick(p.id)}
                className="w-full rounded-radii-md border border-outlineVariant bg-surfaceContainerLowest px-4 py-3 text-left text-body-md text-onSurface hover:bg-pastelLavender"
              >
                {p.firstName} {p.lastName}
              </button>
            </li>
          ))}
        </ul>
      )}
      {query.data && query.data.totalCount > query.data.items.length && (
        <p className="text-body-sm text-onSurfaceVariant">
          {t("analysis.moreResults", { shown: query.data.items.length, total: query.data.totalCount })}
        </p>
      )}
    </div>
  );
}
