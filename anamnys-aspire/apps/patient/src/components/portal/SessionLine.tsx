import { useTranslation } from "react-i18next";
import Badge, { type BadgeTone } from "@anamnys/shared/ui/Badge";
import type { PortalSession, SessionStatus } from "@anamnys/shared/lib/types";
import { sessionWhen } from "./sessionFormat";

const STATUS_TONE: Record<SessionStatus, BadgeTone> = {
  scheduled: "neutral",
  confirmed: "primary",
  attended: "mint",
  cancelled: "neutral",
  no_show: "pink",
};

export function StatusBadge({ status }: { status: SessionStatus }) {
  const { t } = useTranslation();
  return (
    <Badge
      label={t(`patientPortal.sessions.status.${status}`)}
      tone={STATUS_TONE[status]}
      className={status === "cancelled" ? "whitespace-nowrap opacity-60" : "whitespace-nowrap"}
    />
  );
}

export default function SessionLine({ session }: { session: PortalSession }) {
  const { t, i18n } = useTranslation();
  const modality = t(`patientPortal.sessions.modality.${session.modality === "presencial" ? "presencial" : "online"}`);
  return (
    <li className="flex items-center justify-between gap-3 py-3">
      <span className="text-body-md text-onSurface">{sessionWhen(session, i18n.language, modality)}</span>
      <StatusBadge status={session.status} />
    </li>
  );
}
