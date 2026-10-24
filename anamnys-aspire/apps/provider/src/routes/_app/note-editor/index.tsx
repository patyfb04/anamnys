import { useEffect, useState } from "react";
import { createFileRoute, Link, useNavigate } from "@tanstack/react-router";
import { keepPreviousData, useInfiniteQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Loader2, Plus, Search } from "lucide-react";
import { notesApi } from "@anamnys/shared/api/notes";
import type { NoteListFilter } from "@anamnys/shared/lib/types";
import NoteCard from "@/components/NoteCard";
import ThemeFilter from "@/components/themes/ThemeFilter";
import { inputClass } from "@/components/patients/FormField";

export const Route = createFileRoute("/_app/note-editor/")({
  component: NoteListPage,
});

type Filter = NoteListFilter | "all";
const FILTERS: Filter[] = ["pending", "signed", "failed", "all"];

// All of the provider's notes (design/specs/2026-10-15-note-editor-design.md §5.1).
// The name filter lives in component state and goes in a POST body, never in the URL.
function NoteListPage() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const [filter, setFilter] = useState<Filter>("pending");
  const [text, setText] = useState("");
  const [name, setName] = useState("");
  useEffect(() => {
    const timer = setTimeout(() => setName(text.trim()), 300);
    return () => clearTimeout(timer);
  }, [text]);

  const [termId, setTermId] = useState("");
  // A theme matches signed notes only, so choosing one moves the list to "signed".
  const changeTerm = (id: string) => {
    setTermId(id);
    if (id) setFilter("signed");
  };

  const query = useInfiniteQuery({
    queryKey: ["notes", "list", filter, name, termId],
    queryFn: ({ pageParam }) =>
      notesApi.search({
        status: filter === "all" ? undefined : filter,
        name: name || undefined,
        termId: termId || undefined,
        page: pageParam,
      }),
    initialPageParam: 1,
    placeholderData: keepPreviousData,
    getNextPageParam: (last, pages) => (last.hasMore ? pages.length + 1 : undefined),
  });
  const items = query.data?.pages.flatMap((p) => p.items) ?? [];

  return (
    <div className="flex flex-col gap-4 p-4 md:p-8">
      <div className="flex items-center justify-between gap-3">
        <h1 className="text-headline-lg text-onSurface">{t("notes.list.title")}</h1>
        <Link
          to="/note-editor/new"
          className="inline-flex items-center gap-2 px-4 py-2 rounded-radii-md text-label-lg text-onPrimary bg-primary hover:opacity-90"
        >
          <Plus size={18} aria-hidden />
          {t("notes.list.newNote")}
        </Link>
      </div>

      <div className="flex flex-wrap gap-2" role="group" aria-label={t("notes.list.title")}>
        {FILTERS.map((f) => (
          <button
            key={f}
            type="button"
            aria-pressed={filter === f}
            disabled={!!termId && (f === "pending" || f === "failed")}
            onClick={() => setFilter(f)}
            className={`px-3 py-1.5 rounded-full border text-label-lg ${
              filter === f ? "bg-surfaceContainerLowest border-primary text-primary shadow-sm" : "border-outlineVariant text-onSurface"
            } disabled:opacity-50`}
          >
            {t(`notes.list.filters.${f}`)}
          </button>
        ))}
      </div>

      <label className="relative">
        <span className="sr-only">{t("notes.list.searchPlaceholder")}</span>
        <Search size={18} className="absolute left-3 top-1/2 -translate-y-1/2 text-onSurfaceVariant" aria-hidden />
        <input
          className={`${inputClass} pl-10`}
          value={text}
          onChange={(e) => setText(e.target.value)}
          placeholder={t("notes.list.searchPlaceholder")}
          maxLength={100}
        />
      </label>

      <ThemeFilter value={termId} onChange={changeTerm} />
      {termId && <p className="text-body-sm text-onSurfaceVariant">{t("themes.filter.signedOnly")}</p>}

      {query.isLoading ? (
        <div className="flex justify-center py-12">
          <Loader2 className="animate-spin text-primary" size={32} />
        </div>
      ) : query.isError ? (
        <p className="text-error text-center py-12">{t("notes.list.failedToLoad")}</p>
      ) : items.length === 0 ? (
        <p className="text-body-md text-onSurfaceVariant text-center mt-8">{t("notes.list.empty")}</p>
      ) : (
        <div className="flex flex-col gap-2.5">
          {items.map((note) => (
            <NoteCard key={note.id} note={note} showPatient onClick={() => navigate({ to: "/notes/$noteId", params: { noteId: note.id } })} />
          ))}
          {query.hasNextPage && (
            <button
              type="button"
              disabled={query.isFetchingNextPage}
              onClick={() => void query.fetchNextPage()}
              className="self-center px-4 py-2 rounded-radii-lg border border-outlineVariant text-label-lg text-onSurface disabled:opacity-60"
            >
              {t("notes.list.loadMore")}
            </button>
          )}
        </div>
      )}
    </div>
  );
}
