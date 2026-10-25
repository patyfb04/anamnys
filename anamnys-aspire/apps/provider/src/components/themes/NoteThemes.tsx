import { useEffect, useMemo, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Plus, Search, Sparkles, X } from "lucide-react";
import { themesApi } from "@anamnys/shared/api/themes";
import { patientsApi } from "@anamnys/shared/api/patients";
import { ApiError } from "@anamnys/shared/api/client";
import Badge from "@anamnys/shared/ui/Badge";
import type { ThemeSelection } from "@anamnys/shared/lib/types";
import { inputClass } from "@/components/patients/FormField";
import ThemePicker from "./ThemePicker";
import SuggestThemesDialog from "./SuggestThemesDialog";

interface Props {
  noteId: string;
  patientId: string;
  structureStatus?: string;
  sections: { key: string; content: string }[];
  activeExcerptId: string | null;
  onActivate: (excerptId: string | null) => void;
  selection: ThemeSelection | null;
  onSelectionDone: () => void;
}

// Proposals are written after the sections are committed, so they arrive shortly after
// structureStatus turns "done": poll for a couple of minutes, or until one shows up.
const PROPOSAL_POLL_MS = 5000;
const PROPOSAL_WINDOW_MS = 120_000;
// A section reference is located in the background; poll the list until it turns exact.
const LOCATE_POLL_MS = 3000;
const LOCATE_WINDOW_MS = 60_000;

// The "Temas" block of a note (draft or signed): chips, picker, suggestions. Tags can be changed
// after signing (every change is audited server-side); an archived patient makes it read-only.
export default function NoteThemes({
  noteId,
  patientId,
  structureStatus,
  sections,
  activeExcerptId,
  onActivate,
  selection,
  onSelectionDone,
}: Props) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [picking, setPicking] = useState(false);
  const [suggesting, setSuggesting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const blockRef = useRef<HTMLElement>(null);
  const [locatingUntil, setLocatingUntil] = useState(0);
  const startLocating = () => setLocatingUntil(Date.now() + LOCATE_WINDOW_MS);
  const filled = useMemo(() => sections.filter((s) => s.content.trim()), [sections]);
  const [chosenSection, setChosenSection] = useState("");
  const sectionKey = filled.some((s) => s.key === chosenSection) ? chosenSection : (filled[0]?.key ?? "");
  // Sent only when the provider picked one or there is nothing to pick; otherwise the server decides.
  const explicitSection = filled.some((s) => s.key === chosenSection) || filled.length === 1 ? sectionKey : undefined;

  const [polling, setPolling] = useState(structureStatus === "pending");
  useEffect(() => {
    if (structureStatus === "pending") {
      setPolling(true);
      return;
    }
    const id = setTimeout(() => setPolling(false), PROPOSAL_WINDOW_MS);
    return () => clearTimeout(id);
  }, [structureStatus]);

  const tags = useQuery({
    queryKey: ["note-themes", noteId],
    queryFn: () => themesApi.list(noteId),
    refetchInterval: (q) => {
      if (Date.now() < locatingUntil && q.state.data?.some((tag) => tag.excerptKind === "section")) return LOCATE_POLL_MS;
      return polling && !q.state.data?.some((tag) => tag.origin === "ai") ? PROPOSAL_POLL_MS : false;
    },
  });
  const patient = useQuery({ queryKey: ["patient", patientId], queryFn: () => patientsApi.get(patientId) });
  const readOnly = !!patient.data?.archivedAt;
  const taggedTermIds = useMemo(() => new Set((tags.data ?? []).map((tag) => tag.termId)), [tags.data]);

  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: ["note-themes", noteId], exact: true });
    void queryClient.invalidateQueries({ queryKey: ["notes"] });
  };
  const fail = (e: unknown) => {
    const code = e instanceof ApiError ? e.code : undefined;
    setError(t(`themes.errors.${code ?? "generic"}`, { defaultValue: t("themes.errors.generic") }));
    refresh();
  };

  const add = useMutation({
    mutationFn: (termId: string) =>
      themesApi.add(noteId, termId, false, selection ?? (explicitSection ? { sectionKey: explicitSection } : undefined)),
    onSuccess: () => {
      setError(null);
      setPicking(false);
      onSelectionDone();
      startLocating();
      refresh();
    },
    onError: fail,
  });
  const locate = useMutation({
    mutationFn: () => themesApi.locate(noteId),
    onSuccess: () => {
      setError(null);
      startLocating();
      refresh();
    },
    onError: fail,
  });
  const remove = useMutation({
    mutationFn: (tagId: string) => themesApi.remove(noteId, tagId),
    onSuccess: () => {
      setError(null);
      refresh();
    },
    onError: fail,
  });

  // A located reference changes the passage behind a chip: drop the cached passages.
  const refSignature = (tags.data ?? []).map((tag) => `${tag.id}:${tag.excerptId}:${tag.excerptKind}`).join("|");
  useEffect(() => {
    void queryClient.invalidateQueries({ queryKey: ["excerpt"] });
  }, [refSignature, queryClient]);

  useEffect(() => {
    if (selection) blockRef.current?.scrollIntoView({ behavior: "smooth", block: "center" });
  }, [selection]);

  const items = tags.data ?? [];
  const hasSectionRefs = items.some((tag) => tag.excerptKind === "section");
  const showPicker = (picking || !!selection) && !readOnly;
  const actionClass = "inline-flex items-center gap-1 rounded-radii-md px-2 py-1 text-label-md text-primary hover:underline disabled:opacity-60";

  return (
    <section ref={blockRef} aria-labelledby={`themes-${noteId}`} className="mt-6 flex flex-col gap-3 border-t border-outlineVariant pt-4">
      <h3 id={`themes-${noteId}`} className="text-title-md text-onSurface">
        {t("themes.title")}
      </h3>

      {tags.isError && <p className="text-body-md text-error">{t("themes.errors.load")}</p>}
      {tags.isSuccess && items.length === 0 && (
        <p className="text-body-md text-onSurfaceVariant">{t("themes.empty")}</p>
      )}
      {items.length > 0 && (
        <ul className="flex flex-wrap gap-2">
          {items.map((tag) => (
            <li
              key={tag.id}
              className="inline-flex items-center gap-2 rounded-radii-full bg-surfaceContainerHighest py-1 pl-3 pr-1.5 text-label-lg text-onSurface"
              title={tag.parentLabel ?? undefined}
            >
              {tag.excerptId ? (
                <button
                  type="button"
                  onClick={() => onActivate(activeExcerptId === tag.excerptId ? null : tag.excerptId)}
                  aria-pressed={activeExcerptId === tag.excerptId}
                  title={t("themes.showPassage")}
                  className={`rounded-radii-full px-1 hover:underline ${activeExcerptId === tag.excerptId ? "bg-pastelLavender" : ""}`}
                >
                  {tag.label}
                  {tag.termStatus !== "active" && (
                    <span className="text-onSurfaceVariant"> {t("themes.retired")}</span>
                  )}
                </button>
              ) : (
                <span>
                  {tag.label}
                  {tag.termStatus !== "active" && (
                    <span className="text-onSurfaceVariant"> {t("themes.retired")}</span>
                  )}
                </span>
              )}
              {tag.origin === "ai" && <Badge label={t("themes.aiBadge")} tone="lavender" className="!px-2 !py-0.5" />}
              {!readOnly && (
                <button
                  type="button"
                  disabled={remove.isPending}
                  onClick={() => remove.mutate(tag.id)}
                  aria-label={t("themes.removeTheme", { label: tag.label })}
                  className="rounded-radii-full p-1 text-onSurfaceVariant hover:bg-pastelLavender disabled:opacity-60"
                >
                  <X size={14} aria-hidden />
                </button>
              )}
            </li>
          ))}
        </ul>
      )}

      {readOnly ? (
        <p className="text-body-sm text-onSurfaceVariant">{t("themes.archivedReadOnly")}</p>
      ) : (
        <div className="flex flex-wrap items-center gap-2">
          <button type="button" onClick={() => setPicking((v) => !v)} aria-expanded={picking} className={actionClass}>
            <Plus size={16} aria-hidden />
            {t("themes.add")}
          </button>
          <button type="button" onClick={() => setSuggesting(true)} className={actionClass}>
            <Sparkles size={16} aria-hidden />
            {t("themes.suggestAction")}
          </button>
          {hasSectionRefs && (
            <button type="button" disabled={locate.isPending} onClick={() => locate.mutate()} className={actionClass}>
              <Search size={16} aria-hidden />
              {t("themes.locate")}
            </button>
          )}
        </div>
      )}
      {showPicker && (
        <div className="flex flex-col gap-2">
          {selection ? (
            <p className="flex flex-wrap items-center gap-2 text-body-sm text-onSurfaceVariant">
              {t("themes.forSelection")}
              <button type="button" onClick={onSelectionDone} className="text-label-md text-primary hover:underline">
                {t("common.cancel")}
              </button>
            </p>
          ) : (
            filled.length > 1 && (
              <label className="flex flex-col gap-1 text-label-md text-onSurfaceVariant sm:max-w-xs">
                {t("themes.sectionLabel")}
                <select className={inputClass} value={sectionKey} onChange={(e) => setChosenSection(e.target.value)}>
                  {filled.map((s) => (
                    <option key={s.key} value={s.key}>
                      {t(`noteView.sections.${s.key}`, { defaultValue: s.key })}
                    </option>
                  ))}
                </select>
              </label>
            )
          )}
          <ThemePicker taggedTermIds={taggedTermIds} disabled={add.isPending} onPick={(id) => add.mutate(id)} />
        </div>
      )}
      {error && (
        <p role="alert" className="text-body-sm text-error">
          {error}
        </p>
      )}
      <p className="text-body-sm text-onSurfaceVariant">{t("themes.help")}</p>
      {suggesting && <SuggestThemesDialog noteId={noteId} onAccepted={startLocating} onClose={() => setSuggesting(false)} />}
    </section>
  );
}
