import { useState, type ReactNode } from "react";
import { createFileRoute, Link, useNavigate } from "@tanstack/react-router";
import { keepPreviousData, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { documentsApi } from "@anamnys/shared/api/documents";
import { sharesApi } from "@anamnys/shared/api/sharing";
import { toast } from "@anamnys/shared/ui/Toaster";
import ConfirmDialog from "@/components/patients/ConfirmDialog";

type Filter = "active" | "all";
const FILTERS: Filter[] = ["active", "all"];
const MAX_PAGE = 10_000;

export const Route = createFileRoute("/_app/secure-sharing")({
  // Only the filter and the page number in the URL (no PHI).
  validateSearch: (search: Record<string, unknown>): { status: Filter; page: number } => {
    const page = Number(search.page);
    return {
      status: search.status === "all" ? "all" : "active",
      page: Number.isInteger(page) && page >= 1 ? Math.min(page, MAX_PAGE) : 1,
    };
  },
  component: SecureSharingPage,
});

const when = (iso: string) => new Date(iso).toLocaleString("pt-BR", { dateStyle: "short", timeStyle: "short" });
const dayOf = (iso: string) => new Date(iso).toLocaleDateString("pt-BR");

function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="min-w-0">
      <dt className="text-label-md text-onSurfaceVariant">{label}</dt>
      <dd className="wrap-break-word text-body-md text-onSurface">{children}</dd>
    </div>
  );
}

const pagerButton =
  "rounded-radii-md border border-outlineVariant px-4 py-2 text-label-lg text-onSurface hover:bg-pastelLavender disabled:opacity-50 disabled:hover:bg-transparent";

// Every link the provider has created, across documents. Links are created from a signed document's page.
function SecureSharingPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const { status, page } = Route.useSearch();
  const [revoking, setRevoking] = useState<string | null>(null);
  const [pending, setPending] = useState(false);

  const shares = useQuery({
    queryKey: ["shares-overview", status, page],
    queryFn: () => sharesApi.overview(status, page),
    placeholderData: keepPreviousData,
  });
  const kinds = useQuery({ queryKey: ["document-kinds"], queryFn: documentsApi.kinds });
  const kindTitle = (kind: string) => kinds.data?.find((k) => k.kind === kind)?.title ?? kind;

  const go = (next: { status?: Filter; page?: number }) =>
    void navigate({ to: "/secure-sharing", search: { status: next.status ?? status, page: next.page ?? 1 } });

  const revoke = async () => {
    if (!revoking) return;
    setPending(true);
    try {
      await sharesApi.revoke(revoking);
      toast.success(t("sharing.list.revoked"));
      setRevoking(null);
      // The document page lists the same links.
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: ["shares-overview"] }),
        queryClient.invalidateQueries({ queryKey: ["document-shares"] }),
      ]);
    } catch {
      toast.error(t("sharing.list.revokeFailed"));
    } finally {
      setPending(false);
    }
  };

  const data = shares.data;
  const pages = data ? Math.max(1, Math.ceil(data.total / data.pageSize)) : 1;

  return (
    <div className="flex flex-col gap-4 p-4 md:p-8">
      <div>
        <h1 className="text-headline-lg text-onSurface">{t("sharing.title")}</h1>
        <p className="text-body-md text-onSurfaceVariant">{t("sharing.overview.subtitle")}</p>
      </div>

      <div role="group" aria-label={t("sharing.overview.filterLabel")} className="flex flex-wrap gap-2">
        {FILTERS.map((f) => (
          <button
            key={f}
            type="button"
            aria-pressed={status === f}
            onClick={() => go({ status: f })}
            className={`rounded-radii-full border px-4 py-1.5 text-label-lg ${
              status === f
                ? "border-transparent bg-pastelLavender text-onSurface"
                : "border-outlineVariant text-onSurfaceVariant hover:bg-pastelLavender"
            }`}
          >
            {t(f === "active" ? "sharing.overview.filterActive" : "sharing.overview.filterAll")}
          </button>
        ))}
      </div>

      {shares.isPending ? null : shares.isError || !data ? (
        <p role="alert" className="text-body-md text-error">
          {t("sharing.overview.loadFailed")}
        </p>
      ) : data.items.length === 0 ? (
        <p className="text-body-md text-onSurfaceVariant">{t(status === "active" ? "sharing.overview.empty" : "sharing.overview.emptyAll")}</p>
      ) : (
        <ul className="flex flex-col gap-3">
          {data.items.map((item) => {
            const active = item.status === "active";
            return (
              <li key={item.linkId} className="flex flex-col gap-3 rounded-radii-md border border-outlineVariant bg-surfaceContainerLowest p-4">
                <div className="flex flex-wrap items-center justify-between gap-2">
                  <span
                    className={`rounded-radii-full px-2 py-0.5 text-label-md ${active ? "bg-pastelLavender text-onSurface" : "bg-surfaceContainerLow text-onSurfaceVariant"}`}
                  >
                    {t(`sharing.status.${item.status}`)}
                  </span>
                  {active && (
                    <button type="button" onClick={() => setRevoking(item.linkId)} className="text-label-lg text-error hover:underline">
                      {t("sharing.list.revoke")}
                    </button>
                  )}
                </div>
                <dl className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-4">
                  <Field label={t("sharing.overview.patient")}>{item.patientName}</Field>
                  <Field label={t("sharing.overview.document")}>
                    <Link to="/documents/$documentId" params={{ documentId: item.documentId }} className="text-primary hover:underline">
                      {kindTitle(item.documentKind)} · {dayOf(item.signedAt)}
                    </Link>
                    {item.newerVersionExists && <span className="block text-body-sm text-onSurfaceVariant">{t("sharing.overview.newerVersion")}</span>}
                  </Field>
                  <Field label={t("sharing.overview.recipient")}>
                    {item.recipientName} · {item.recipientRole}
                  </Field>
                  <Field label={t("sharing.overview.purpose")}>{item.purpose}</Field>
                  <Field label={t("sharing.overview.created")}>{when(item.createdAt)}</Field>
                  <Field label={t("sharing.overview.expires")}>{when(item.expiresAt)}</Field>
                  <Field label={t("sharing.overview.views")}>
                    {item.viewCap === null
                      ? t("sharing.list.views", { count: item.viewsUsed })
                      : t("sharing.list.viewsCapped", { used: item.viewsUsed, cap: item.viewCap })}
                  </Field>
                  <Field label={t("sharing.overview.lastAccess")}>
                    {item.lastAccessAt ? when(item.lastAccessAt) : t("sharing.overview.noAccess")}
                  </Field>
                </dl>
              </li>
            );
          })}
        </ul>
      )}

      {data && pages > 1 && (
        <nav className="flex items-center justify-between gap-3" aria-label={t("sharing.overview.pageOf", { page, total: pages })}>
          <button type="button" disabled={page <= 1} onClick={() => go({ page: page - 1 })} className={pagerButton}>
            {t("sharing.overview.previous")}
          </button>
          <span className="text-body-md text-onSurfaceVariant">{t("sharing.overview.pageOf", { page, total: pages })}</span>
          <button type="button" disabled={page >= pages} onClick={() => go({ page: page + 1 })} className={pagerButton}>
            {t("sharing.overview.next")}
          </button>
        </nav>
      )}

      <ConfirmDialog
        open={revoking !== null}
        onOpenChange={(open) => {
          if (!open) setRevoking(null);
        }}
        title={t("sharing.list.revokeTitle")}
        description={t("sharing.list.revokeBody")}
        confirmLabel={t("sharing.list.revoke")}
        destructive
        pending={pending}
        onConfirm={() => void revoke()}
      />
    </div>
  );
}
