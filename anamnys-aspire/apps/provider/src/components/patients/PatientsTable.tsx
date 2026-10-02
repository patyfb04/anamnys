import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { ArrowDown, ArrowUp, ArrowUpDown, FileText } from "lucide-react";
import Avatar from "@anamnys/shared/ui/Avatar";
import type { PatientListItem, PatientSortBy, SortDir } from "@anamnys/shared/lib/types";
import NoteStatusBadge from "./NoteStatusBadge";
import { formatDate, formatTime } from "./format";

interface Props {
  items: PatientListItem[];
  sortBy: PatientSortBy;
  sortDir: SortDir;
  onSort: (column: PatientSortBy) => void;
  onOpen: (patientId: string) => void;
}

// Desktop (md+) layout of the patient list, per specs/UI/PatientList. Below md the page
// renders PatientCards instead.
export default function PatientsTable({ items, sortBy, sortDir, onSort, onOpen }: Props) {
  const { t, i18n } = useTranslation();
  const locale = i18n.language;

  const sortable = (column: PatientSortBy, label: string, className = "") => {
    const active = column === sortBy;
    const Icon = !active ? ArrowUpDown : sortDir === "asc" ? ArrowUp : ArrowDown;
    return (
      <th
        scope="col"
        aria-sort={active ? (sortDir === "asc" ? "ascending" : "descending") : "none"}
        className={`px-4 py-3 font-normal ${className}`}
      >
        <button
          type="button"
          onClick={() => onSort(column)}
          className={`inline-flex items-center gap-1 text-left uppercase tracking-wider hover:text-primary transition-colors ${
            active ? "text-primary" : ""
          }`}
        >
          {label}
          <Icon size={14} className={active ? "" : "opacity-40"} />
        </button>
      </th>
    );
  };

  return (
    <table className="w-full text-left">
      <thead className="bg-surfaceContainer border-b border-outlineVariant text-label-md text-onSurfaceVariant">
        <tr>
          {sortable("name", t("patients.list.columns.patient"))}
          {sortable("lastVisit", t("patients.list.columns.lastVisit"))}
          {sortable("nextVisit", t("patients.list.columns.nextVisit"))}
          <th scope="col" className="px-4 py-3 font-normal uppercase tracking-wider text-center">
            {t("patients.list.columns.details")}
          </th>
          {sortable("noteStatus", t("patients.list.columns.status"), "text-center")}
        </tr>
      </thead>
      <tbody className="divide-y divide-outlineVariant">
        {items.map((patient) => {
          const fullName = `${patient.firstName} ${patient.lastName}`;
          return (
            <tr
              key={patient.id}
              onClick={() => onOpen(patient.id)}
              className="group cursor-pointer hover:bg-surfaceContainerLow transition-colors"
            >
              <td className="px-4 py-3">
                <div className="flex items-center gap-3 min-w-0">
                  <Avatar name={fullName} size={40} />
                  <div className="min-w-0">
                    <p className="text-label-lg text-onSurface truncate group-hover:text-primary transition-colors">
                      {fullName}
                    </p>
                    <p className="text-label-md text-outline truncate">{patient.email ?? t("patients.list.noPortal")}</p>
                  </div>
                </div>
              </td>
              <Cell primary={formatDate(patient.lastVisit, locale)} />
              <Cell
                primary={formatDate(patient.nextAppointmentAt, locale)}
                secondary={formatTime(patient.nextAppointmentAt, locale)}
              />
              <td className="px-4 py-3 text-center">
                <button
                  type="button"
                  onClick={(e) => {
                    e.stopPropagation();
                    onOpen(patient.id);
                  }}
                  className="inline-flex items-center gap-1 whitespace-nowrap text-label-md text-primary hover:underline"
                >
                  <FileText size={16} />
                  {t("patients.list.viewDetails")}
                </button>
              </td>
              <td className="px-4 py-3 text-center">
                <NoteStatusBadge status={patient.noteStatus} />
              </td>
            </tr>
          );
        })}
      </tbody>
    </table>
  );
}

function Cell({ primary, secondary }: { primary: ReactNode; secondary?: ReactNode }) {
  return (
    <td className="px-4 py-3 whitespace-nowrap">
      <p className="text-body-md text-onSurface">{primary}</p>
      {secondary ? <p className="text-label-md text-outline">{secondary}</p> : null}
    </td>
  );
}
