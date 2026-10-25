import { useEffect, useState } from "react";
import { createFileRoute, Link, useNavigate } from "@tanstack/react-router";
import { keepPreviousData, useInfiniteQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Loader2, Plus, Search } from "lucide-react";
import { documentsApi } from "@anamnys/shared/api/documents";
import type { DocumentStatus } from "@anamnys/shared/lib/types";
import Card from "@anamnys/shared/ui/Card";
import ReceivedDocumentsList from "@/components/externalDocuments/ReceivedDocumentsList";
import DocumentStatusBadge from "@/components/documents/DocumentStatusBadge";
import NewDocumentModal from "@/components/documents/NewDocumentModal";
import { inputClass } from "@/components/patients/FormField";

export const Route = createFileRoute("/_app/documents/")({
  component: DocumentListPage,
});

type Filter = DocumentStatus | "all";
const FILTERS: Filter[] = ["draft", "signed", "superseded", "all"];
type Tab = "issued" | "received";
const TABS: Tab[] = ["issued", "received"];

// All of the provider's psychological documents.
// The name filter lives in component state and goes in a POST body, never in the URL.
function DocumentListPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [filter, setFilter] = useState<Filter>("all");
  const [text, setText] = useState("");
  const [name, setName] = useState("");
  const [creating, setCreating] = useState(false);
  const [tab, setTab] = useState<Tab>("issued");
  useEffect(() => {
    const timer = setTimeout(() => setName(text.trim()), 300);
    return () => clearTimeout(timer);
  }, [text]);

  const query = useInfiniteQuery({
    enabled: tab === "issued",
    queryKey: ["documents", "list", filter, name],
    queryFn: ({ pageParam }) =>
      documentsApi.search({ status: filter === "all" ? undefined : filter, name: name || undefined, page: pageParam }),
    initialPageParam: 1,
    placeholderData: keepPreviousData,
    getNextPageParam: (last, pages) => (last.hasMore ? pages.length + 1 : undefined),
  });
  const items = query.data?.pages.flatMap((p) => p.items) ?? [];

  return (
    <div className="flex flex-col gap-4 p-4 md:p-8">
      <div className="flex items-center justify-between gap-3">
        <h1 className="text-headline-lg text-onSurface">{t("documents.title")}</h1>
        {tab === "issued" && (
          <button
            type="button"
            onClick={() => setCreating(true)}
            className="inline-flex items-center gap-2 px-4 py-2 rounded-radii-md text-label-lg text-onPrimary bg-primary hover:opacity-90"
          >
            <Plus size={18} aria-hidden />
            {t("documents.new")}
          </button>
        )}
      </div>

      <div className="flex gap-1 border-b border-outlineVariant" role="tablist" aria-label={t("documents.title")}>
        {TABS.map((id) => (
          <button
            key={id}
            type="button"
            role="tab"
            aria-selected={tab === id}
            onClick={() => setTab(id)}
            className={`px-4 py-2 text-label-lg -mb-px border-b-2 hover:bg-pastelLavender ${
              tab === id ? "border-primary text-primary" : "border-transparent text-onSurface"
            }`}
          >
            {t(`externalDocuments.tabs.${id}`)}
          </button>
        ))}
      </div>

      {tab === "received" ? (
        <ReceivedDocumentsList />
      ) : (
        <>

        <div className="flex flex-wrap gap-2" role="group" aria-label={t("documents.title")}>
          {FILTERS.map((f) => (
            <button
              key={f}
              type="button"
              aria-pressed={filter === f}
              onClick={() => setFilter(f)}
              className={`px-3 py-1.5 rounded-full border text-label-lg ${
                filter === f ? "bg-surfaceContainerLowest border-primary text-primary shadow-sm" : "border-outlineVariant text-onSurface"
              }`}
            >
              {t(`documents.list.filters.${f}`)}
            </button>
          ))}
        </div>

        <label className="relative">
          <span className="sr-only">{t("documents.list.searchPlaceholder")}</span>
          <Search size={18} className="absolute left-3 top-1/2 -translate-y-1/2 text-onSurfaceVariant" aria-hidden />
          <input
            className={`${inputClass} pl-10`}
            value={text}
            onChange={(e) => setText(e.target.value)}
            placeholder={t("documents.list.searchPlaceholder")}
            maxLength={100}
          />
        </label>

        {query.isLoading ? (
          <div className="flex justify-center py-12">
            <Loader2 className="animate-spin text-primary" size={32} />
          </div>
        ) : query.isError ? (
          <p className="text-error text-center py-12">{t("documents.listLoadFailed")}</p>
        ) : items.length === 0 ? (
          <p className="text-body-md text-onSurfaceVariant text-center mt-8">{t("documents.list.empty")}</p>
        ) : (
          <div className="flex flex-col gap-2.5">
            {items.map((d) => (
              <Card key={d.id} onClick={() => navigate({ to: "/documents/$documentId", params: { documentId: d.id } })}>
                <div className="flex items-center justify-between gap-2 mb-2">
                  <span className="text-label-md text-onSurfaceVariant normal-case">
                    {d.signedAt
                      ? t("documents.signedOn", { date: new Date(d.signedAt).toLocaleDateString("pt-BR") })
                      : t("documents.createdOn", { date: new Date(d.createdAt).toLocaleDateString("pt-BR") })}
                  </span>
                  <DocumentStatusBadge status={d.status} />
                </div>
                <p className="text-headline-sm text-[16px] text-onSurface mb-1 truncate">{d.title}</p>
                <p className="text-body-md text-onSurfaceVariant truncate">
                  <Link
                    to="/patients/$patientId"
                    params={{ patientId: d.patientId }}
                    onClick={(e) => e.stopPropagation()}
                    className="hover:underline"
                  >
                    {d.patientName}
                  </Link>
                </p>
              </Card>
            ))}
            {query.hasNextPage && (
              <button
                type="button"
                disabled={query.isFetchingNextPage}
                onClick={() => void query.fetchNextPage()}
                className="self-center px-4 py-2 rounded-radii-lg border border-outlineVariant text-label-lg text-onSurface disabled:opacity-60"
              >
                {t("documents.list.loadMore")}
              </button>
            )}
          </div>
        )}

        </>
      )}

      {creating && <NewDocumentModal onClose={() => setCreating(false)} />}
    </div>
  );
}
