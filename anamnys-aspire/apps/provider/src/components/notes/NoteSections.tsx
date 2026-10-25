import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { useQueryClient } from "@tanstack/react-query";
import { Loader2, Pencil } from "lucide-react";
import { isAxiosError } from "axios";
import { notesApi } from "@anamnys/shared/api/notes";
import type { ExcerptPassage, NoteSectionView, ThemeSelection } from "@anamnys/shared/lib/types";

interface Props {
  sections: NoteSectionView[];
  editable: boolean;
  noteId: string;
  onSaved: () => void;
  highlight?: ExcerptPassage | null;
  onMark?: (selection: ThemeSelection) => void;
}

// Mirrors the server limit on a stored quote.
const MAX_QUOTE = 200;

// The structured note: one tonal block per section, in format order. Empty sections stay visible
// and say so — the AI never fills them in. When editable, each block can be edited in place.
export default function NoteSections({ sections, editable, noteId, onSaved, highlight, onMark }: Props) {
  const ordered = [...sections].sort((a, b) => a.position - b.position);

  return (
    <div className="flex flex-col gap-3">
      {ordered.map((s) => (
        <SectionBlock
          key={s.key}
          section={s}
          editable={editable}
          noteId={noteId}
          onSaved={onSaved}
          highlight={highlight}
          onMark={onMark}
        />
      ))}
    </div>
  );
}

function SectionBlock({ section, editable, noteId, onSaved, highlight, onMark }: Omit<Props, "sections"> & { section: NoteSectionView }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
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
  const sectionRef = useRef<HTMLElement>(null);
  const textRef = useRef<HTMLParagraphElement>(null);
  const markRef = useRef<HTMLElement>(null);
  const [range, setRange] = useState<{ start: number; end: number } | null>(null);

  // An exact highlight needs the text it was computed on; if the section changed since, fall back
  // to marking the whole section.
  const active = highlight && highlight.sectionKey === section.key ? highlight : null;
  const exact =
    active?.kind === "exact" &&
    active.start !== undefined &&
    active.end !== undefined &&
    active.sectionText === current.content
      ? { start: active.start, end: active.end }
      : null;
  const activeId = active?.id;
  useEffect(() => {
    if (!activeId) return;
    (markRef.current ?? sectionRef.current)?.scrollIntoView({ behavior: "smooth", block: "center" });
  }, [activeId]);

  // The provider selects text inside the section; offsets are into the section text, trimmed.
  const content = current.content;
  useEffect(() => {
    if (!onMark || editing) {
      setRange(null);
      return;
    }
    const read = () => {
      const el = textRef.current;
      const sel = window.getSelection();
      if (!el || !sel || sel.rangeCount === 0 || sel.isCollapsed) return setRange(null);
      const r = sel.getRangeAt(0);
      if (!r.intersectsNode(el)) return setRange(null);
      // A selection that runs past the paragraph (triple-click) is clamped to the section text.
      const offsetOf = (node: Node, offset: number) => {
        const upTo = document.createRange();
        upTo.selectNodeContents(el);
        upTo.setEnd(node, offset);
        return upTo.toString().length;
      };
      let start = el.contains(r.startContainer) ? offsetOf(r.startContainer, r.startOffset) : 0;
      let end = el.contains(r.endContainer) ? offsetOf(r.endContainer, r.endOffset) : content.length;
      while (start < end && /\s/.test(content[start])) start++;
      while (end > start && /\s/.test(content[end - 1])) end--;
      setRange(end > start ? { start, end } : null);
    };
    document.addEventListener("selectionchange", read);
    return () => document.removeEventListener("selectionchange", read);
  }, [onMark, editing, content]);

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
      void queryClient.invalidateQueries({ queryKey: ["excerpt"] });
      onSaved();
    } catch (e) {
      const reason = isAxiosError(e) ? (e.response?.data as { reason?: string } | undefined)?.reason : undefined;
      setError(reason === "locked" || reason === "stale" ? reason : "generic");
    } finally {
      setSaving(false);
    }
  };

  return (
    <section
      ref={sectionRef}
      className={`rounded-radii-lg bg-surfaceContainerLow p-4 ${active && !exact ? "ring-2 ring-primary" : ""}`}
    >
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
        <>
          <p ref={textRef} className="whitespace-pre-wrap text-body-md text-onSurface">
            {exact ? (
              <>
                {current.content.slice(0, exact.start)}
                <mark
                  ref={markRef}
                  className="rounded-sm bg-pastelLavender text-onSurface underline decoration-primary decoration-2 underline-offset-2"
                >
                  {current.content.slice(exact.start, exact.end)}
                </mark>
                {current.content.slice(exact.end)}
              </>
            ) : (
              current.content
            )}
          </p>
          {range && onMark && (
            <div className="mt-2" role="status">
              {range.end - range.start > MAX_QUOTE ? (
                <p className="text-body-sm text-onSurfaceVariant">
                  {t("noteView.markTooLong", { max: MAX_QUOTE, count: range.end - range.start })}
                </p>
              ) : (
                <button
                  type="button"
                  onMouseDown={(e) => e.preventDefault()}
                  onClick={() => {
                    onMark({ sectionKey: section.key, start: range.start, end: range.end });
                    window.getSelection()?.removeAllRanges();
                  }}
                  className="rounded-radii-md px-2 py-1 text-label-md text-primary hover:underline"
                >
                  {t("noteView.markTheme")}
                </button>
              )}
            </div>
          )}
        </>
      ) : (
        <p className="text-body-md italic text-onSurfaceVariant">{t("noteView.emptySection")}</p>
      )}
    </section>
  );
}
