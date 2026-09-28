import { useTranslation } from "react-i18next";
import { ChevronRight } from "lucide-react";
import Avatar from "@anamnys/shared/ui/Avatar";
import type { PatientListItem } from "@anamnys/shared/lib/types";
import NoteStatusBadge from "./NoteStatusBadge";
import { formatDate, formatTime } from "./format";

interface Props {
  items: PatientListItem[];
  onOpen: (patientId: string) => void;
}

// Mobile (below md) layout of the patient list: one card per patient. Sorting lives in
// PatientSortSelect because there are no column headers to click here.
export default function PatientCards({ items, onOpen }: Props) {
  const { t, i18n } = useTranslation();
  const locale = i18n.language;

  return (
    <ul className="divide-y divide-outlineVariant">
      {items.map((patient) => {
        const fullName = `${patient.firstName} ${patient.lastName}`;
        return (
          <li key={patient.id}>
            <button
              type="button"
              onClick={() => onOpen(patient.id)}
              className="w-full flex items-center gap-3 px-4 py-3 text-left hover:bg-surfaceContainerLow transition-colors"
            >
              <Avatar name={fullName} size={40} />
              <div className="flex-1 min-w-0">
                <p className="text-label-lg text-onSurface truncate">{fullName}</p>
                <p className="text-label-md text-outline truncate">{patient.email ?? t("patients.list.noPortal")}</p>
                <p className="text-body-md text-onSurfaceVariant mt-1">
                  {t("patients.list.columns.nextVisit")}:{" "}
                  {patient.nextAppointmentAt
                    ? `${formatDate(patient.nextAppointmentAt, locale)} ${formatTime(patient.nextAppointmentAt, locale)}`
                    : "—"}
                </p>
                <div className="mt-2">
                  <NoteStatusBadge status={patient.noteStatus} />
                </div>
              </div>
              <ChevronRight size={20} className="text-outline shrink-0" />
            </button>
          </li>
        );
      })}
    </ul>
  );
}
