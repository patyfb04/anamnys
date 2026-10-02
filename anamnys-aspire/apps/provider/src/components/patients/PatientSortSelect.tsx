import { useTranslation } from "react-i18next";
import type { PatientSortBy, SortDir } from "@anamnys/shared/lib/types";

const COLUMNS: PatientSortBy[] = ["nextVisit", "lastVisit", "name", "noteStatus"];
const DIRECTIONS: SortDir[] = ["asc", "desc"];

interface Props {
  sortBy: PatientSortBy;
  sortDir: SortDir;
  onChange: (sortBy: PatientSortBy, sortDir: SortDir) => void;
}

// Mobile stand-in for the table's clickable column headers.
export default function PatientSortSelect({ sortBy, sortDir, onChange }: Props) {
  const { t } = useTranslation();
  return (
    <label className="flex items-center gap-2 whitespace-nowrap text-label-md text-onSurfaceVariant">
      {t("patients.list.sortLabel")}
      <select
        value={`${sortBy}:${sortDir}`}
        onChange={(e) => {
          const [nextSortBy, nextSortDir] = e.target.value.split(":") as [PatientSortBy, SortDir];
          onChange(nextSortBy, nextSortDir);
        }}
        className="flex-1 bg-surfaceContainerLowest border border-outlineVariant rounded-radii-md px-2 py-1.5 text-body-md text-onSurface"
      >
        {COLUMNS.flatMap((column) =>
          DIRECTIONS.map((dir) => (
            <option key={`${column}:${dir}`} value={`${column}:${dir}`}>
              {t(`patients.list.sort.${column}`)} ({t(`patients.list.dir.${dir}`)})
            </option>
          )),
        )}
      </select>
    </label>
  );
}
