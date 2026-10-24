import { useState } from "react";
import { useMutation, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Plus, Trash2 } from "lucide-react";
import { adminThemesApi, type AdminThemeTerm, type NewThemeTerm } from "@anamnys/shared/api/adminThemes";
import Modal from "./Modal";
import { Body, CancelButton, ErrorNote, Field, SubmitButton, termName } from "./dialogParts";
import { VOCABULARY_KEY } from "./vocabularyKey";

function useVocabularyMutation<T>(fn: () => Promise<T>, onClose: () => void) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: fn,
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: VOCABULARY_KEY });
      onClose();
    },
  });
}

const SELECT_CLASS =
  "w-full rounded-radii-md border border-outlineVariant bg-surfaceContainerLow px-3.5 py-2.5 text-body-lg text-onSurface outline-none focus:border-primary";

export function AddTermDialog({
  terms,
  parentId: initialParentId,
  onClose,
}: {
  terms: AdminThemeTerm[];
  parentId?: string;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const [code, setCode] = useState("");
  const [label, setLabel] = useState("");
  const [parentId, setParentId] = useState(initialParentId ?? "");
  const topLevel = terms.filter((x) => x.parentId === null && x.status === "active");
  const mutation = useVocabularyMutation(
    () => adminThemesApi.create({ code, label, parentId: parentId || undefined }),
    onClose,
  );

  return (
    <Modal
      title={t("admin.vocabulary.add.title")}
      onClose={onClose}
      footer={
        <>
          <CancelButton onClick={onClose} />
          <SubmitButton
            label={t("admin.vocabulary.add.submit")}
            pending={mutation.isPending}
            disabled={!code || !label.trim()}
            onClick={() => mutation.mutate()}
          />
        </>
      }
    >
      <Body>
        <Field label={t("admin.vocabulary.fields.code")} value={code} onChange={setCode} maxLength={60} hint={t("admin.vocabulary.fields.codeHint")} />
        <Field label={t("admin.vocabulary.fields.label")} value={label} onChange={setLabel} maxLength={80} />
        <label className="block">
          <span className="block text-label-sm text-onSurfaceVariant mb-1.5">{t("admin.vocabulary.fields.parent")}</span>
          <select value={parentId} onChange={(e) => setParentId(e.target.value)} className={SELECT_CLASS}>
            <option value="">{t("admin.vocabulary.fields.noParent")}</option>
            {topLevel.map((x) => (
              <option key={x.id} value={x.id}>
                {x.label}
              </option>
            ))}
          </select>
        </label>
        <ErrorNote error={mutation.error} />
      </Body>
    </Modal>
  );
}

export function RenameTermDialog({ term, onClose }: { term: AdminThemeTerm; onClose: () => void }) {
  const { t } = useTranslation();
  const [label, setLabel] = useState(term.label);
  const mutation = useVocabularyMutation(() => adminThemesApi.rename(term.id, label), onClose);

  return (
    <Modal
      title={t("admin.vocabulary.rename.title", { label: term.label })}
      onClose={onClose}
      footer={
        <>
          <CancelButton onClick={onClose} />
          <SubmitButton
            label={t("common.save")}
            pending={mutation.isPending}
            disabled={!label.trim()}
            onClick={() => mutation.mutate()}
          />
        </>
      }
    >
      <Body>
        <Field label={t("admin.vocabulary.fields.label")} value={label} onChange={setLabel} maxLength={80} />
        <ErrorNote error={mutation.error} />
      </Body>
    </Modal>
  );
}

export function RetireTermDialog({ term, onClose }: { term: AdminThemeTerm; onClose: () => void }) {
  const { t } = useTranslation();
  const mutation = useVocabularyMutation(() => adminThemesApi.retire(term.id), onClose);

  return (
    <Modal
      title={t("admin.vocabulary.retire.title", { label: term.label })}
      onClose={onClose}
      footer={
        <>
          <CancelButton onClick={onClose} />
          <SubmitButton
            label={t("admin.vocabulary.retire.submit")}
            pending={mutation.isPending}
            onClick={() => mutation.mutate()}
          />
        </>
      }
    >
      <Body>
        <p className="text-body-md text-onSurface">{t("admin.vocabulary.retire.body")}</p>
        <ErrorNote error={mutation.error} />
      </Body>
    </Modal>
  );
}

export function SplitTermDialog({ term, onClose }: { term: AdminThemeTerm; onClose: () => void }) {
  const { t } = useTranslation();
  const [rows, setRows] = useState<NewThemeTerm[]>([
    { code: "", label: "" },
    { code: "", label: "" },
  ]);
  const mutation = useVocabularyMutation(() => adminThemesApi.split(term.id, rows), onClose);
  const update = (i: number, patch: Partial<NewThemeTerm>) =>
    setRows((prev) => prev.map((r, idx) => (idx === i ? { ...r, ...patch } : r)));
  const complete = rows.every((r) => r.code && r.label.trim());

  return (
    <Modal
      title={t("admin.vocabulary.split.title", { label: term.label })}
      onClose={onClose}
      footer={
        <>
          <CancelButton onClick={onClose} />
          <SubmitButton
            label={t("admin.vocabulary.split.submit")}
            pending={mutation.isPending}
            disabled={!complete}
            onClick={() => mutation.mutate()}
          />
        </>
      }
    >
      <Body>
        <p className="text-body-md text-onSurface">{t("admin.vocabulary.split.body")}</p>
        <p className="text-body-md font-medium text-onSurface">
          {t("admin.vocabulary.stays", { count: term.activeTagCount })}
        </p>
        {rows.map((row, i) => (
          <div key={i} className="flex flex-col gap-2 rounded-radii-md border border-outlineVariant p-3">
            <div className="flex items-center justify-between">
              <span className="text-label-md text-onSurfaceVariant">{t("admin.vocabulary.split.newTerm", { n: i + 1 })}</span>
              {rows.length > 2 && (
                <button
                  type="button"
                  onClick={() => setRows((prev) => prev.filter((_, idx) => idx !== i))}
                  aria-label={t("common.remove")}
                  className="p-1.5 rounded-radii-full text-onSurfaceVariant hover:bg-pastelLavender"
                >
                  <Trash2 size={16} />
                </button>
              )}
            </div>
            <Field label={t("admin.vocabulary.fields.code")} value={row.code} onChange={(v) => update(i, { code: v })} maxLength={60} />
            <Field label={t("admin.vocabulary.fields.label")} value={row.label} onChange={(v) => update(i, { label: v })} maxLength={80} />
          </div>
        ))}
        {rows.length < 10 && (
          <button
            type="button"
            onClick={() => setRows((prev) => [...prev, { code: "", label: "" }])}
            className="flex items-center gap-1.5 self-start text-label-lg text-primary hover:underline"
          >
            <Plus size={16} />
            {t("admin.vocabulary.split.addRow")}
          </button>
        )}
        <ErrorNote error={mutation.error} />
      </Body>
    </Modal>
  );
}

export function MergeTermsDialog({
  terms,
  sources,
  onClose,
}: {
  terms: AdminThemeTerm[];
  sources: AdminThemeTerm[];
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const [mode, setMode] = useState<"existing" | "new">("existing");
  const [existingId, setExistingId] = useState("");
  const [code, setCode] = useState("");
  const [label, setLabel] = useState("");
  const sourceIds = sources.map((s) => s.id);
  const candidates = terms.filter(
    (x) => x.status === "active" && x.parentId === sources[0].parentId && !sourceIds.includes(x.id),
  );
  const mutation = useVocabularyMutation(
    () => adminThemesApi.merge(sourceIds, mode === "existing" ? { existingId } : { code, label }),
    onClose,
  );
  const total = sources.reduce((sum, s) => sum + s.activeTagCount, 0);
  const ready = mode === "existing" ? !!existingId : !!code && !!label.trim();

  return (
    <Modal
      title={t("admin.vocabulary.merge.title")}
      onClose={onClose}
      footer={
        <>
          <CancelButton onClick={onClose} />
          <SubmitButton
            label={t("admin.vocabulary.merge.submit")}
            pending={mutation.isPending}
            disabled={!ready}
            onClick={() => mutation.mutate()}
          />
        </>
      }
    >
      <Body>
        <ul className="list-disc pl-5 text-body-md text-onSurface">
          {sources.map((s) => (
            <li key={s.id}>{termName(s, terms)}</li>
          ))}
        </ul>
        <p className="text-body-md font-medium text-onSurface">{t("admin.vocabulary.merge.stays", { count: total })}</p>
        <fieldset className="flex gap-4">
          <label className="flex items-center gap-2 text-body-md text-onSurface">
            <input type="radio" checked={mode === "existing"} onChange={() => setMode("existing")} className="accent-primary" />
            {t("admin.vocabulary.merge.existing")}
          </label>
          <label className="flex items-center gap-2 text-body-md text-onSurface">
            <input type="radio" checked={mode === "new"} onChange={() => setMode("new")} className="accent-primary" />
            {t("admin.vocabulary.merge.new")}
          </label>
        </fieldset>
        {mode === "existing" ? (
          <select value={existingId} onChange={(e) => setExistingId(e.target.value)} className={SELECT_CLASS}>
            <option value="">{t("admin.vocabulary.merge.choose")}</option>
            {candidates.map((x) => (
              <option key={x.id} value={x.id}>
                {termName(x, terms)}
              </option>
            ))}
          </select>
        ) : (
          <>
            <Field label={t("admin.vocabulary.fields.code")} value={code} onChange={setCode} maxLength={60} hint={t("admin.vocabulary.fields.codeHint")} />
            <Field label={t("admin.vocabulary.fields.label")} value={label} onChange={setLabel} maxLength={80} />
          </>
        )}
        <ErrorNote error={mutation.error} />
      </Body>
    </Modal>
  );
}
