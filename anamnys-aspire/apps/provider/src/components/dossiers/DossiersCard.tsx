import { useEffect, useState } from "react";
import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useRouterState } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { Archive } from "lucide-react";
import { dossiersApi } from "@anamnys/shared/api/dossiers";
import type { DossierItem } from "@anamnys/shared/lib/types";
import { toast } from "@anamnys/shared/ui/Toaster";
import DetailCard from "@/components/patients/detail/DetailCard";
import { goToCard } from "@/components/patients/detail/cardAnchors";
import GenerateDossierModal from "./GenerateDossierModal";

const POLL_MS = 5_000;
const CARD_ID = "dossiers";
// The server and the receipt use the practice's time zone, so the card does too.
const dateFormat = new Intl.DateTimeFormat("pt-BR", { day: "2-digit", month: "2-digit", year: "numeric", timeZone: "America/Sao_Paulo" });
const linkClass = "text-label-lg text-primary hover:underline disabled:opacity-50";
const pagerButton =
  "rounded-radii-md px-3 py-1 text-label-md text-primary hover:bg-pastelLavender disabled:cursor-default disabled:text-onSurfaceVariant disabled:hover:bg-transparent";

// "YYYY-MM-DD" practice dates are formatted without a time zone shift.
function day(iso: string): string {
  const [y, m, d] = iso.split("-");
  return `${d}/${m}/${y}`;
}

function saveBlob(blob: Blob, name: string) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = name;
  a.click();
  window.setTimeout(() => URL.revokeObjectURL(url), 60_000);
}

const pad = (n: number) => String(n).padStart(3, "0");

// Patient record card: request a dossier (ZIP of the signed record), follow progress, download it
// and its delivery receipt. 5 rows per page, paged by the server. Polls while any row is queued or running.
export default function DossiersCard({ patientId, readOnly }: { patientId: string; readOnly: boolean }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [page, setPage] = useState(1);
  const [requesting, setRequesting] = useState<DossierItem | "new" | null>(null);

  const list = useQuery({
    queryKey: ["dossiers", patientId, page],
    queryFn: () => dossiersApi.list(patientId, page),
    placeholderData: keepPreviousData,
    refetchInterval: (q) =>
      q.state.data?.items.some((d) => d.status === "queued" || d.status === "running") ? POLL_MS : false,
  });
  const items = list.data?.items ?? [];
  const total = list.data?.total ?? 0;
  const pageSize = list.data?.pageSize ?? 5;
  const pages = Math.max(1, Math.ceil(total / pageSize));

  // The bell opens the patient page with #dossiers.
  const hash = useRouterState({ select: (s) => s.location.hash });
  useEffect(() => {
    if (hash === CARD_ID) goToCard(CARD_ID);
  }, [hash]);

  const download = useMutation({
    mutationFn: (id: string) => dossiersApi.download(id),
    onSuccess: ({ url }) => window.location.assign(url),
    onError: () => {
      toast.error(t("dossier.downloadFailed"));
      void queryClient.invalidateQueries({ queryKey: ["dossiers", patientId] });
    },
  });

  const fetchFile = async (load: () => Promise<Blob>, name: string, failed: string) => {
    try {
      saveBlob(await load(), name);
    } catch {
      toast.error(t(failed));
    }
  };

  const periodText = (d: DossierItem) =>
    d.periodFrom && d.periodTo
      ? t("dossier.period", { from: day(d.periodFrom), to: day(d.periodTo) })
      : d.periodFrom
        ? t("dossier.periodFromOnly", { from: day(d.periodFrom) })
        : d.periodTo
          ? t("dossier.periodToOnly", { to: day(d.periodTo) })
          : t("dossier.periodAll");

  const statusText = (d: DossierItem) =>
    t(`dossier.status.${d.status}`, { date: d.expiresAt ? dateFormat.format(new Date(d.expiresAt)) : "" });

  return (
    <DetailCard
      collapsibleId={CARD_ID}
      title={t("dossier.title")}
      icon={<Archive size={20} />}
      action={
        !readOnly && (
          <button type="button" onClick={() => setRequesting("new")} className="inline-flex items-center gap-1 px-3 py-1.5 text-label-lg text-primary hover:underline">
            {t("dossier.generate")}
          </button>
        )
      }
    >
      {list.isPending ? null : list.isError ? (
        <p className="text-body-md text-error">{t("dossier.loadFailed")}</p>
      ) : total === 0 ? (
        <p className="text-body-md text-onSurfaceVariant">{t("dossier.empty")}</p>
      ) : (
        <>
          <ul className="flex flex-col divide-y divide-outlineVariant">
            {items.map((d) => {
              const base = `anamnys-dossie-${pad(d.number)}`;
              return (
                <li key={d.id} className="flex flex-col gap-1 py-3">
                  <div className="flex flex-wrap items-baseline justify-between gap-2">
                    <span className="text-label-lg text-onSurface">
                      {t("dossier.number", { number: pad(d.number) })} · {t("dossier.requestedOn", { date: dateFormat.format(new Date(d.requestedAt)) })}
                    </span>
                    <span
                      role={d.status === "failed" ? "alert" : "status"}
                      className={`text-label-md ${d.status === "failed" ? "text-error" : "text-onSurfaceVariant"}`}
                    >
                      {statusText(d)}
                    </span>
                  </div>
                  <p className="text-body-sm text-onSurfaceVariant">
                    {periodText(d)} · {t(`dossier.reason.${d.reason}`)}
                  </p>
                  {d.status === "done" && d.integrityVerified === false && (
                    <p className="text-body-sm text-error">{t("dossier.integrityProblem")}</p>
                  )}
                  <div className="flex flex-wrap gap-x-4 gap-y-1">
                    {d.status === "done" && (
                      <button type="button" disabled={download.isPending} onClick={() => download.mutate(d.id)} className={linkClass}>
                        {t("dossier.download")}
                      </button>
                    )}
                    {(d.status === "done" || d.status === "expired") && (
                      <>
                        <button
                          type="button"
                          className={linkClass}
                          onClick={() => void fetchFile(() => dossiersApi.receiptPdf(d.id), `${base}-comprovante.pdf`, "dossier.receiptFailed")}
                        >
                          {t("dossier.receipt")}
                        </button>
                        {d.anchored && (
                          <button
                            type="button"
                            className={linkClass}
                            onClick={() => void fetchFile(() => dossiersApi.timestampToken(d.id), `${base}.tsr`, "dossier.timestampFailed")}
                          >
                            {t("dossier.timestamp")}
                          </button>
                        )}
                      </>
                    )}
                    {d.status === "failed" && !readOnly && (
                      <button type="button" className={linkClass} onClick={() => setRequesting(d)}>
                        {t("dossier.retry")}
                      </button>
                    )}
                  </div>
                </li>
              );
            })}
          </ul>
          {pages > 1 && (
            <div className="mt-2 flex flex-wrap items-center gap-2">
              <button type="button" className={pagerButton} disabled={page <= 1} onClick={() => setPage(page - 1)}>
                {t("compliance.pager.previous")}
              </button>
              <span className="text-body-sm text-onSurfaceVariant" aria-live="polite">
                {t("compliance.pager.range", { from: (page - 1) * pageSize + 1, to: (page - 1) * pageSize + items.length, total })}
              </span>
              <button type="button" className={pagerButton} disabled={page >= pages} onClick={() => setPage(page + 1)}>
                {t("compliance.pager.next")}
              </button>
            </div>
          )}
        </>
      )}

      {requesting && (
        <GenerateDossierModal
          patientId={patientId}
          onClose={() => setRequesting(null)}
          onRequested={() => setPage(1)}
          initial={requesting === "new" ? undefined : requesting}
        />
      )}
    </DetailCard>
  );
}
