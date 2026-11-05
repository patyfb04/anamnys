import { Link } from "@tanstack/react-router";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { ShieldCheck, ShieldAlert } from "lucide-react";
import { integrityApi } from "@anamnys/shared/api/integrity";

interface Props {
  subject: { type: "note" | "document"; id: string };
  /** Changes when the record is signed or corrected, so the badge is read again. */
  version?: string | null;
}

// Badge next to the signature of a signed record. Drafts and unknown records (404) show nothing.
export default function IntegrityBadge({ subject, version }: Props) {
  const { t } = useTranslation();
  const { data } = useQuery({
    queryKey: ["integrity", subject.type, subject.id, version ?? ""],
    queryFn: () => (subject.type === "note" ? integrityApi.forNote(subject.id) : integrityApi.forDocument(subject.id)),
    retry: false,
  });

  if (!data || !data.sealedAt || data.status === "unsealed" || data.status === "discarded") return null;
  const date = new Date(data.sealedAt).toLocaleDateString("pt-BR");

  if (data.status === "intact") {
    return (
      <span className="inline-flex flex-wrap items-center gap-1 text-body-sm text-onSurfaceVariant">
        <ShieldCheck size={14} aria-hidden className="text-primary" />
        {t(data.retroactive ? "integrity.badge.intactRetroactive" : "integrity.badge.intact", { date })}
        {!data.anchored && <span>· {t("integrity.badge.awaiting")}</span>}
      </span>
    );
  }

  return (
    <span role="alert" className="inline-flex flex-wrap items-center gap-1 text-body-sm text-error">
      <ShieldAlert size={14} aria-hidden />
      {t(data.status === "content_changed" ? "integrity.badge.contentChanged" : "integrity.badge.chainBroken", { date })}
      <Link to="/compliance" hash="integridade" className="underline hover:no-underline">
        {t("integrity.badge.seeCompliance")}
      </Link>
    </span>
  );
}
