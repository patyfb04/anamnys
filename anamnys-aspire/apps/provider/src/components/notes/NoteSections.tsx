import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { Loader2, Pencil } from "lucide-react";
import { isAxiosError } from "axios";
import { notesApi } from "@anamnys/shared/api/notes";
import type { NoteSectionView } from "@anamnys/shared/lib/types";

interface Props {
  sections: NoteSectionView[];
  editable: boolean;
  noteId: string;
  onSaved: () => void;
}

// The structured note: one tonal block per section, in format order. Empty sections stay visible
// and say so — the AI never fills them in. When editable, each block can be edited in place.
export default function NoteSections({ sections, editable, noteId, onSaved }: Props) {
  const ordered = [...sections].sort((a, b) => a.position - b.position);

  return (
    <div className="flex flex-col gap-3">
      {ordered.map((s) => (
        <SectionBlock key={s.key} section={s} editable={editable} noteId={noteId} onSaved={onSaved} />
      ))}
    </div>
  );
}

function SectionBlock({ section, editable, noteId, onSaved }: Omit<Props, "sections"> & { section: NoteSectionView }) {
  const { t } = useTranslation();
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState(section.content);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  // The last successful save, shown (and its revision used) until the refetched note catches up,
  // so a quick second edit does not go out with a stale revision.
  const [saved, setSaved] = useState<{ content: string; revision: number } | null>(null);
  const current = saved && saved.revision > section.revision ? saved : section;
  const edited = !!section.editedAt || current !== section;
  const areaRef = useRef<HTMLTextAreaElement>(null);

  // Locking (signing, restructuring) can happen while the editor is open: drop out of edit mode.
  useEffect(() => {
    if (!editable) setEditing(false);
  }, [editable]);

  useLayoutEffect(() => {
    const el = areaRef.current;
    if (!el) return;
    el.style.height = "auto";
    el.style.height = `${el.scrollHeight}px`;
  }, [draft, editing]);

  const start = () => {
    setDraft(current.content);
    setError(null);
    setEditing(true);
  };

  const save = async () => {
    setSaving(true);
    setError(null);
    try {
      const { revision } = await notesApi.editSection(noteId, section.key, draft, current.revision);
      setSaved({ content: draft.trim(), revision });
      setEditing(false);
      onSaved();
    } catch (e) {
      const reason = isAxiosError(e) ? (e.response?.data as { reason?: string } | undefined)?.reason : undefined;
      setError(reason === "locked" || reason === "stale" ? reason : "generic");
    } finally {
      setSaving(false);
    }
  };

  return (
    <section className="rounded-radii-lg bg-surfaceContainerLow p-4">
      <div className="mb-1 flex flex-wrap items-center gap-2">
        <h3 className="text-label-lg uppercase tracking-wide text-primary">{t(`noteView.sections.${section.key}`)}</h3>
        {edited && (
          <span className="rounded-radii-full bg-primaryFixed px-2 py-0.5 text-label-sm text-onPrimaryFixedVariant">
            {t("noteView.editedByYou")}
          </span>
        )}
        {editable && !editing && (
          <button
            type="button"
            onClick={start}
            className="ml-auto inline-flex items-center gap-1 rounded-radii-md px-2 py-1 text-label-md text-primary hover:bg-pastelLavender"
          >
            <Pencil size={14} aria-hidden />
            {t("noteView.edit")}
          </button>
        )}
      </div>
      {editing ? (
        <div className="flex flex-col gap-2">
          <textarea
            ref={areaRef}
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            maxLength={20000}
            rows={3}
            disabled={saving}
            aria-label={t(`noteView.sections.${section.key}`)}
            className="w-full resize-none overflow-hidden rounded-radii-md border border-outlineVariant bg-surfaceContainerLowest p-3 text-body-md text-onSurface focus:outline-none focus:ring-2 focus:ring-primary"
          />
          {error && (
            <p role="alert" className="text-body-sm text-error">
              {t(`noteView.editErrors.${error}`)}
            </p>
          )}
          <div className="flex gap-2">
            <button
              type="button"
              disabled={saving}
              onClick={save}
              className="inline-flex items-center gap-2 rounded-radii-lg bg-primary px-4 py-2 text-label-lg text-onPrimary disabled:opacity-60"
            >
              {saving && <Loader2 size={14} className="animate-spin" aria-hidden />}
              {t("noteView.save")}
            </button>
            <button
              type="button"
              disabled={saving}
              onClick={() => setEditing(false)}
              className="rounded-radii-lg px-4 py-2 text-label-lg text-primary hover:bg-pastelLavender disabled:opacity-60"
            >
              {t("noteView.cancel")}
            </button>
          </div>
        </div>
      ) : current.content ? (
        <p className="whitespace-pre-wrap text-body-md text-onSurface">{current.content}</p>
      ) : (
        <p className="text-body-md italic text-onSurfaceVariant">{t("noteView.emptySection")}</p>
      )}
    </section>
  );
}
