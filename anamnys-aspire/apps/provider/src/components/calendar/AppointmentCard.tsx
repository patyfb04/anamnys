import { useTranslation } from "react-i18next";
import { User, Video } from "lucide-react";
import type { Appointment } from "@anamnys/shared/lib/types";
import { formatTime } from "./calendarTime";
import { STATUS_STYLE } from "./status";

interface Props {
  appointment: Appointment;
  top: number;
  height: number;
  onClick: () => void;
}

export default function AppointmentCard({ appointment: a, top, height, onClick }: Props) {
  const { t } = useTranslation();
  const ModalityIcon = a.modality === "online" ? Video : User;
  const start = formatTime(new Date(a.startsAt), a.timezone);
  const end = formatTime(new Date(a.endsAt), a.timezone);
  return (
    <button
      type="button"
      onClick={(e) => {
        e.stopPropagation();
        onClick();
      }}
      style={{ top, height }}
      className={`absolute inset-x-1 ${a.status === "cancelled" ? "z-[1]" : "z-[2]"} overflow-hidden rounded-radii-md border-l-4 px-2 py-1 text-left text-label-md shadow-sm hover:brightness-95 ${STATUS_STYLE[a.status]}`}
    >
      <div className="flex items-center justify-between gap-1">
        <span className="font-semibold">{start}–{end}</span>
        <ModalityIcon size={14} aria-label={t(`calendar.modality.${a.modality}`)} />
      </div>
      <div className="truncate text-body-md">{a.patientName}</div>
      <div className="truncate uppercase tracking-wide">{t(`calendar.status.${a.status}`)}</div>
    </button>
  );
}
