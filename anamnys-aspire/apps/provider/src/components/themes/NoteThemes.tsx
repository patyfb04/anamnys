import { useEffect, useMemo, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Plus, Sparkles, X } from "lucide-react";
import { themesApi } from "@anamnys/shared/api/themes";
import { patientsApi } from "@anamnys/shared/api/patients";
import { ApiError } from "@anamnys/shared/api/client";
import Badge from "@anamnys/shared/ui/Badge";
import ThemePicker from "./ThemePicker";
import SuggestThemesDialog from "./SuggestThemesDialog";

interface Props {
  noteId: string;
  patientId: string;
  structureStatus?: string;
}

// Proposals are written after the sections are committed, so they arrive shortly after
// structureStatus turns "done": poll for a couple of minutes, or until one shows up.
const PROPOSAL_POLL_MS = 5000;
const PROPOSAL_WINDOW_MS = 120_000;

// The "Temas" block of a note (draft or signed): chips, picker, suggestions. Tags can be changed
// after signing (every change is audited server-side); an archived patient makes it read-only.
export default function NoteThemes({ noteId, patientId, structureStatus }: Props) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const [picking, setPicking] = useState(false);
  const [suggesting, setSuggesting] = useState(false);
  const [error, setError] = useState<string | null>(null);

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
    refetchInterval: (q) => (polling && !q.state.data?.some((tag) => tag.origin === "ai") ? PROPOSAL_POLL_MS : false),
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
    mutationFn: (termId: string) => themesApi.add(noteId, termId),
    onSuccess: () => {
      setError(null);
      setPicking(false);
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

  const items = tags.data ?? [];
  const actionClass = "inline-flex items-center gap-1 rounded-radii-md px-2 py-1 text-label-md text-primary hover:underline disabled:opacity-60";

  return (
    <section aria-labelledby={`themes-${noteId}`} className="mt-6 flex flex-col gap-3 border-t border-outlineVariant pt-4">
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
              <span>
                {tag.label}
                {tag.termStatus !== "active" && (
                  <span className="text-onSurfaceVariant"> {t("themes.retired")}</span>
                )}
              </span>
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
        </div>
      )}
      {picking && !readOnly && (
        <ThemePicker taggedTermIds={taggedTermIds} disabled={add.isPending} onPick={(id) => add.mutate(id)} />
      )}
      {error && (
        <p role="alert" className="text-body-sm text-error">
          {error}
        </p>
      )}
      <p className="text-body-sm text-onSurfaceVariant">{t("themes.help")}</p>
      {suggesting && <SuggestThemesDialog noteId={noteId} onClose={() => setSuggesting(false)} />}
    </section>
  );
}
