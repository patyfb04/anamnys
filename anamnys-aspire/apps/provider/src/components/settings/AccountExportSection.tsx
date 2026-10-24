import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { Loader2 } from "lucide-react";
import { useTranslation } from "react-i18next";
import { exportsApi } from "@anamnys/shared/api/exports";
import { ApiError } from "@anamnys/shared/api/client";
import { toast } from "@anamnys/shared/ui/Toaster";
import type { AccountExport } from "@anamnys/shared/lib/types";

const POLL_MS = 5_000;
const dateFormat = new Intl.DateTimeFormat("pt-BR", { day: "2-digit", month: "2-digit", year: "numeric" });

function formatSize(bytes: number | null): string {
  if (bytes == null) return "";
  if (bytes < 1024) return `${bytes} B`;
  const units = ["KB", "MB", "GB"];
  let value = bytes / 1024;
  let i = 0;
  while (value >= 1024 && i < units.length - 1) {
    value /= 1024;
    i++;
  }
  return `${value.toLocaleString("pt-BR", { maximumFractionDigits: 1 })} ${units[i]}`;
}

function isExpired(e: AccountExport): boolean {
  return e.status === "expired" || (e.status === "done" && !!e.expiresAt && new Date(e.expiresAt).getTime() <= Date.now());
}

// Full-account export: request, follow progress, download the presigned archive.
export default function AccountExportSection() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { data, isPending, isError } = useQuery({
    queryKey: ["account-export"],
    queryFn: exportsApi.latest,
    refetchInterval: (query) => {
      const status = query.state.data?.status;
      return status === "queued" || status === "running" ? POLL_MS : false;
    },
  });
  const refresh = () => queryClient.invalidateQueries({ queryKey: ["account-export"] });

  const request = useMutation({
    mutationFn: exportsApi.request,
    onSuccess: refresh,
    onError: (error) => {
      if (error instanceof ApiError && error.code === "export_in_progress") {
        toast.error(t("settings.accountExport.inProgress"));
        void refresh();
      } else {
        toast.error(t("settings.accountExport.requestFailed"));
      }
    },
  });

  const download = useMutation({
    mutationFn: (id: string) => exportsApi.download(id),
    onSuccess: ({ url }) => {
      window.location.assign(url);
    },
    onError: (error) => {
      toast.error(t("settings.accountExport.downloadFailed"));
      if (error instanceof ApiError && error.status === 409) void refresh();
    },
  });

  const expired = data ? isExpired(data) : false;
  const inProgress = data?.status === "queued" || data?.status === "running";

  const generate = (label: string, outlined = false) => (
    <button
      type="button"
      disabled={request.isPending}
      onClick={() => request.mutate()}
      className={
        outlined
          ? "self-start border border-outlineVariant text-onSurface rounded-radii-md px-4 py-2 text-label-lg hover:bg-pastelLavender disabled:opacity-50"
          : "self-start bg-primary text-onPrimary rounded-radii-md px-4 py-2 text-label-lg hover:opacity-90 disabled:opacity-50"
      }
    >
      {label}
    </button>
  );

  return (
    <section
      id="exportar-dados"
      className="bg-surfaceContainerLowest border border-outlineVariant rounded-radii-md p-6 flex flex-col gap-4 scroll-mt-6"
    >
      <div>
        <h2 className="text-title-md text-onSurface">{t("settings.accountExport.title")}</h2>
        <p className="text-body-md text-onSurfaceVariant">{t("settings.accountExport.description")}</p>
      </div>
      <p className="text-body-md text-onSurface">{t("settings.accountExport.included")}</p>
      <p className="text-body-md text-onSurface">{t("settings.accountExport.excluded")}</p>
      <p role="note" className="text-body-md text-onSurface font-bold">
        {t("settings.accountExport.warning")}
      </p>

      {inProgress && (
        <p role="status" className="flex items-center gap-2 text-body-md text-onSurfaceVariant">
          <Loader2 size={18} className="animate-spin" aria-hidden />
          {t(`settings.accountExport.${data.status}`)}
        </p>
      )}

      {data?.status === "done" && !expired && (
        <div className="flex flex-col gap-3">
          <p className="text-body-md text-onSurface">
            {t("settings.accountExport.done", {
              size: formatSize(data.sizeBytes),
              date: data.expiresAt ? dateFormat.format(new Date(data.expiresAt)) : "",
            })}
          </p>
          <button
            type="button"
            disabled={download.isPending}
            onClick={() => download.mutate(data.id)}
            className="self-start bg-primary text-onPrimary rounded-radii-md px-4 py-2 text-label-lg hover:opacity-90 disabled:opacity-50"
          >
            {t("settings.accountExport.download")}
          </button>
          {generate(t("settings.accountExport.generateNew"), true)}
        </div>
      )}

      {data?.status === "failed" && (
        <div className="flex flex-col gap-3">
          <p role="alert" className="text-body-md text-error">
            {t("settings.accountExport.failed")}
          </p>
          {generate(t("settings.accountExport.retry"))}
        </div>
      )}

      {expired && (
        <div className="flex flex-col gap-3">
          <p className="text-body-md text-onSurfaceVariant">{t("settings.accountExport.expired")}</p>
          {generate(t("settings.accountExport.generateNew"))}
        </div>
      )}

      {isError && !data && (
        <p role="alert" className="text-body-md text-error">
          {t("settings.accountExport.loadFailed")}
        </p>
      )}

      {!isPending && !isError && !data && generate(t("settings.accountExport.generate"))}
    </section>
  );
}
