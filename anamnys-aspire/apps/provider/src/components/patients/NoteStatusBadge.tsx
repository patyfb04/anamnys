import { useTranslation } from "react-i18next";
import Badge from "@anamnys/shared/ui/Badge";
import type { NoteStatusGroup } from "@anamnys/shared/lib/types";

const DOT = <span aria-hidden className="w-1.5 h-1.5 rounded-radii-full bg-current" />;

// Status of the patient's most recent note, in the three groups the list uses.
export default function NoteStatusBadge({ status }: { status: NoteStatusGroup }) {
  const { t } = useTranslation();
  const label = t(`patients.list.status.${status}`);
  if (status === "signed") return <Badge label={label} tone="mint" icon={DOT} className="whitespace-nowrap" />;
  if (status === "pending") return <Badge label={label} tone="neutral" icon={DOT} className="whitespace-nowrap" />;
  return <Badge label={label} tone="neutral" className="whitespace-nowrap opacity-60" />;
}
