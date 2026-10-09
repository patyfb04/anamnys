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

interface Props {
  session: PortalSession;
  onConfirm?: (session: PortalSession) => void;
  confirming?: boolean;
}

export default function SessionLine({ session, onConfirm, confirming }: Props) {
  const { t, i18n } = useTranslation();
  const modality = t(`patientPortal.sessions.modality.${session.modality === "presencial" ? "presencial" : "online"}`);
  return (
    <li className="flex items-center justify-between gap-3 py-3">
      <span className="text-body-md text-onSurface">{sessionWhen(session, i18n.language, modality)}</span>
      <span className="flex items-center gap-2">
        {onConfirm && session.status === "scheduled" && (
          <button
            type="button"
            disabled={confirming}
            onClick={() => onConfirm(session)}
            className="px-3 py-1 border border-outlineVariant rounded-radii-md text-label-md text-primary hover:bg-surfaceContainerLow disabled:opacity-60"
          >
            {t("patientPortal.sessions.confirm")}
          </button>
        )}
        <StatusBadge status={session.status} />
      </span>
    </li>
  );
}
