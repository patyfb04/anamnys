import { useState } from "react";
import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { FileDown, ShieldCheck } from "lucide-react";
import { integrityApi } from "@anamnys/shared/api/integrity";
import { toast } from "@anamnys/shared/ui/Toaster";
import DetailCard from "@/components/patients/detail/DetailCard";

const dateTime = new Intl.DateTimeFormat("pt-BR", { dateStyle: "short", timeStyle: "short", timeZone: "America/Sao_Paulo" });
const buttonClass =
  "inline-flex items-center justify-center gap-2 rounded-radii-md border border-outlineVariant px-3 py-1.5 text-label-lg text-onSurface hover:bg-pastelLavender disabled:opacity-50 disabled:hover:bg-transparent";

// Chain state of the provider's sealed records (F-45).
export default function IntegrityCard() {
  const { t } = useTranslation();
  const [downloading, setDownloading] = useState(false);
  const { data, isPending, isError, isFetching, refetch } = useQuery({
    queryKey: ["integrity-summary"],
    queryFn: integrityApi.summary,
    staleTime: 0,
  });

  const download = async () => {
    setDownloading(true);
    try {
      const url = URL.createObjectURL(await integrityApi.reportPdf());
      const a = document.createElement("a");
      a.href = url;
      a.download = "comprovante-integridade.pdf";
      a.click();
      window.setTimeout(() => URL.revokeObjectURL(url), 60_000);
    } catch {
      toast.error(t("integrity.card.downloadFailed"));
    } finally {
      setDownloading(false);
    }
  };

  const problem = data?.firstProblem;
  // Every kind the server emits has copy; a future kind shows a generic text, never the raw code.
  const problemText = problem
    ? (() => {
        const kind = t(`integrity.card.kind.${problem.kind}`, { defaultValue: t("integrity.card.kind.unknown") });
        if (problem.subjectType === null) {
          return t("integrity.card.problemNoSubject", { sequence: problem.sequence, kind });
        }
        const subject = t(`integrity.card.subject.${problem.subjectType}`, { defaultValue: t("integrity.card.subject.unknown") });
        return t("integrity.card.problem", { sequence: problem.sequence, subject, kind });
      })()
    : null;
  return (
    <DetailCard collapsibleId="integrity" title={t("integrity.card.title")} icon={<ShieldCheck size={20} />}>
      {isPending ? null : isError ? (
        <p role="alert" className="text-body-md text-error">
          {t("integrity.card.loadFailed")}
        </p>
      ) : (
        <div className="flex flex-col gap-3 text-body-md text-onSurface">
          <p>
            <span className="text-onSurfaceVariant">{t("integrity.card.state")}: </span>
            {problemText ? (
              <span className="text-error">{problemText}</span>
            ) : (
              t("integrity.card.intact")
            )}
          </p>
          <p>
            <span className="text-onSurfaceVariant">{t("integrity.card.total")}: </span>
            {data.totalSeals}
            {data.discarded > 0 && (
              <span className="text-body-sm text-onSurfaceVariant"> · {t("integrity.card.discarded", { count: data.discarded })}</span>
            )}
          </p>
          <p>
            <span className="text-onSurfaceVariant">{t("integrity.card.lastAnchor")}: </span>
            {data.lastAnchor
              ? t("integrity.card.lastAnchorValue", { date: dateTime.format(new Date(data.lastAnchor.anchoredAt)), tsa: data.lastAnchor.tsaUrl })
              : t("integrity.card.noAnchor")}
          </p>
          <p className="text-body-sm text-onSurfaceVariant">{t("integrity.card.verifiedAt", { date: dateTime.format(new Date(data.verifiedAt)) })}</p>
          <div className="flex flex-wrap gap-2">
            <button type="button" disabled={isFetching} onClick={() => void refetch()} className={buttonClass}>
              {isFetching ? t("integrity.card.verifying") : t("integrity.card.verify")}
            </button>
            <button type="button" disabled={downloading} onClick={() => void download()} className={buttonClass}>
              <FileDown size={16} aria-hidden />
              {t("integrity.card.download")}
            </button>
          </div>
        </div>
      )}
    </DetailCard>
  );
}
