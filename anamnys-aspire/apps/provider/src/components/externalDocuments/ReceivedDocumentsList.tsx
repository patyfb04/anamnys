import { useEffect, useState } from "react";
import { keepPreviousData, useInfiniteQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Loader2, Search } from "lucide-react";
import { externalDocumentsApi } from "@anamnys/shared/api/externalDocuments";
import type { ExternalDocumentKind } from "@anamnys/shared/lib/types";
import { inputClass } from "@/components/patients/FormField";
import ExternalDocumentRow from "./ExternalDocumentRow";
import { KINDS, hasPending } from "./fileCheck";

const POLL_MS = 3000;

// "Recebidos" tab of the documents page: every received file of the provider. The name filter lives
// in component state and goes in a POST body, never in the URL. Delete is offered for every row;
// for an archived patient the server answers 409 `archived` and the row shows that message.
export default function ReceivedDocumentsList() {
  const { t } = useTranslation();
  const [kind, setKind] = useState<ExternalDocumentKind | "all">("all");
  const [text, setText] = useState("");
  const [name, setName] = useState("");
  useEffect(() => {
    const timer = setTimeout(() => setName(text.trim()), 300);
    return () => clearTimeout(timer);
  }, [text]);

  const query = useInfiniteQuery({
    queryKey: ["external-documents", "list", kind, name],
    queryFn: ({ pageParam }) =>
      externalDocumentsApi.search({ kind: kind === "all" ? undefined : kind, name: name || undefined, page: pageParam }),
    initialPageParam: 1,
    placeholderData: keepPreviousData,
    getNextPageParam: (last, pages) => (last.hasMore ? pages.length + 1 : undefined),
    refetchInterval: (q) => (hasPending(q.state.data?.pages.flatMap((p) => p.items)) ? POLL_MS : false),
  });
  const items = query.data?.pages.flatMap((p) => p.items) ?? [];

  return (
    <div className="flex flex-col gap-4">
      <div className="flex flex-wrap gap-2" role="group" aria-label={t("externalDocuments.kind")}>
        {(["all", ...KINDS] as const).map((k) => (
          <button
            key={k}
            type="button"
            aria-pressed={kind === k}
            onClick={() => setKind(k)}
            className={`px-3 py-1.5 rounded-full border text-label-lg ${
              kind === k ? "bg-surfaceContainerLowest border-primary text-primary shadow-sm" : "border-outlineVariant text-onSurface"
            }`}
          >
            {k === "all" ? t("externalDocuments.allKinds") : t(`externalDocuments.kinds.${k}`)}
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
        <p className="text-error text-center py-12">{t("externalDocuments.loadFailed")}</p>
      ) : items.length === 0 ? (
        <p className="text-body-md text-onSurfaceVariant text-center mt-8">{t("externalDocuments.listEmpty")}</p>
      ) : (
        <ul className="flex flex-col divide-y divide-outlineVariant rounded-radii-xl border border-outlineVariant bg-surfaceContainerLowest px-5">
          {items.map((d) => (
            <ExternalDocumentRow key={d.id} doc={d} readOnly={d.patientArchived === true} showPatient />
          ))}
        </ul>
      )}
      {query.hasNextPage && (
        <button
          type="button"
          disabled={query.isFetchingNextPage}
          onClick={() => void query.fetchNextPage()}
          className="self-center px-4 py-2 rounded-radii-lg border border-outlineVariant text-label-lg text-onSurface hover:bg-pastelLavender disabled:opacity-60"
        >
          {t("documents.list.loadMore")}
        </button>
      )}
    </div>
  );
}
