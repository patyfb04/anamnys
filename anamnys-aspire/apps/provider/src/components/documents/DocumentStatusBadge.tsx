import { useTranslation } from "react-i18next";
import Badge, { type BadgeTone } from "@anamnys/shared/ui/Badge";
import type { DocumentStatus } from "@anamnys/shared/lib/types";

const TONE: Record<DocumentStatus, BadgeTone> = { draft: "blue", signed: "mint", superseded: "neutral" };

export default function DocumentStatusBadge({ status }: { status: DocumentStatus }) {
  const { t } = useTranslation();
  return <Badge label={t(`documents.status.${status}`)} tone={TONE[status]} />;
}
