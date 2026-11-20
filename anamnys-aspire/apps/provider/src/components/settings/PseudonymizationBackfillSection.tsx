import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { ApiError } from "@anamnys/shared/api/client";
import { pseudonymizationApi } from "@anamnys/shared/api/pseudonymization";
import { toast } from "@anamnys/shared/ui/Toaster";

const POLL_MS = 5_000;
const KEY = ["pseudonymization-backfill"];

// Converts the transcripts stored before F-27 so third-party names are replaced by codes.
export default function PseudonymizationBackfillSection() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { data, isError, refetch } = useQuery({
    queryKey: KEY,
    queryFn: pseudonymizationApi.backfillStatus,
    refetchInterval: (q) => (q.state.data?.running ? POLL_MS : false),
  });

  const start = useMutation({
    mutationFn: pseudonymizationApi.startBackfill,
    onSuccess: (status) => queryClient.setQueryData(KEY, status),
    onError: (error) => {
      if (error instanceof ApiError && error.status === 409) {
        toast.error(t("pseudonymization.backfill.alreadyRunning"));
      } else {
        toast.error(t("pseudonymization.backfill.startFailed"));
      }
      void queryClient.invalidateQueries({ queryKey: KEY });
    },
  });

  const handled = data ? data.done + data.failed : 0;
  const percent = data && data.total > 0 ? Math.min(100, Math.round((handled / data.total) * 100)) : 0;
  const finished = data && !data.running && data.finishedAt ? new Date(data.finishedAt) : null;

  return (
    <section className="bg-surfaceContainerLowest border border-outlineVariant rounded-radii-md p-6 flex flex-col gap-4">
      <div>
        <h2 className="text-title-md text-onSurface">{t("pseudonymization.backfill.title")}</h2>
        <p className="text-body-md text-onSurfaceVariant">{t("pseudonymization.backfill.description")}</p>
      </div>

      {isError && (
        <p className="flex flex-wrap items-center gap-2 text-body-sm text-error">
          <span role="alert">{t("pseudonymization.backfill.loadFailed")}</span>
          <button type="button" onClick={() => void refetch()} className="text-label-md text-primary hover:underline">
            {t("pseudonymization.failed.retry")}
          </button>
        </p>
      )}

      {data && (
        <>
          <p className="text-body-md text-onSurface">{t("pseudonymization.backfill.pending", { count: data.pending })}</p>

          {data.running && (
            <div className="flex flex-col gap-1">
              <div
                role="progressbar"
                aria-valuemin={0}
                aria-valuemax={data.total}
                aria-valuenow={Math.min(handled, data.total)}
                aria-label={t("pseudonymization.backfill.progressLabel")}
                className="h-2 w-full max-w-md overflow-hidden rounded-radii-full bg-surfaceContainerHigh"
              >
                <div className="h-full bg-primary transition-all" style={{ width: `${percent}%` }} />
              </div>
              <span className="text-body-sm text-onSurfaceVariant">
                {t("pseudonymization.backfill.progress", { done: handled, total: data.total })}
              </span>
            </div>
          )}

          {data.failed > 0 && (
            <p className="text-body-sm text-error">{t("pseudonymization.backfill.failedCount", { count: data.failed })}</p>
          )}

          {finished && (
            <p className="text-body-sm text-onSurfaceVariant">
              {t("pseudonymization.backfill.finishedAt", {
                date: finished.toLocaleDateString("pt-BR"),
                time: finished.toLocaleTimeString("pt-BR", { hour: "2-digit", minute: "2-digit" }),
              })}
            </p>
          )}

          <button
            type="button"
            disabled={data.pending === 0 || data.running || start.isPending}
            onClick={() => start.mutate()}
            className="self-start bg-primary text-onPrimary rounded-radii-md px-4 py-2 text-label-lg disabled:opacity-50"
          >
            {data.running ? t("pseudonymization.backfill.running") : t("pseudonymization.backfill.start")}
          </button>
        </>
      )}
    </section>
  );
}
