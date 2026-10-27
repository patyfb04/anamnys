import { useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { ApiError } from "@anamnys/shared/api/client";
import { documentFactsApi } from "@anamnys/shared/api/documentFacts";
import type { DocumentFact, ExternalDocumentItem } from "@anamnys/shared/lib/types";
import Badge from "@anamnys/shared/ui/Badge";
import { toast } from "@anamnys/shared/ui/Toaster";
import { inputClass } from "@/components/patients/FormField";
import Modal from "@/components/patients/Modal";
import DocumentPassageModal from "./DocumentPassageModal";
import MedicationFactModal from "./MedicationFactModal";
import { FACT_KINDS, useFactsRefresh } from "./factsShared";

const POLL_MS = 3000;
function errorKey(e: unknown): string {
  if (e instanceof ApiError) {
    if (e.code === "archived" || e.code === "not_pending" || e.code === "not_available") return e.code;
    if (e.status === 400) return "invalid_value";
  }
  return "failed";
}

// "Ver fatos": what the reader found in the document, grouped by kind. A fact only counts for the
// record once the provider confirms or corrects it. Archived patients: read-only.
export default function DocumentFactsModal({
  doc,
  readOnly,
  onOpenOriginal,
  onClose,
}: {
  doc: ExternalDocumentItem;
  readOnly: boolean;
  onOpenOriginal: () => void;
  onClose: () => void;
}) {
  const { t } = useTranslation();
  const refresh = useFactsRefresh(doc);
  const [passageId, setPassageId] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const queryClient = useQueryClient();
  const [medicationFact, setMedicationFact] = useState<DocumentFact | null>(null);
  // Facts turned into a record in this session (the list reports the same once reloaded).
  const [recordedIds, setRecordedIds] = useState<Set<string>>(new Set());

  const query = useQuery({
    queryKey: ["document-facts", doc.id],
    queryFn: () => documentFactsApi.list(doc.id),
    refetchInterval: (q) => {
      const d = q.state.data;
      return d && (d.extracting || d.readStatus === "pending" || d.readStatus === "reading") ? POLL_MS : false;
    },
  });
  const data = query.data;
  const facts = data?.facts ?? [];
  const pending = facts.filter((f) => f.status === "pending");
  // Medication facts are confirmed through their own form, so "Confirmar todos" leaves them out.
  const pendingPlain = pending.filter((f) => f.kind !== "medication");
  const busy = !!data && (data.extracting || data.readStatus === "pending" || data.readStatus === "reading");

  const onError = (e: unknown) => {
    setError(t(`documentFacts.errors.${errorKey(e)}`));
    void refresh();
  };
  const act = useMutation({
    mutationFn: (fn: () => Promise<unknown>) => fn(),
    onSuccess: async () => {
      setError(null);
      await refresh();
    },
    onError,
  });
  const confirmAll = useMutation({
    mutationFn: async () => {
      // A fact reviewed meanwhile (elsewhere or by a re-extraction) answers not_pending: skip it.
      for (const f of pendingPlain) {
        try {
          await documentFactsApi.confirm(doc.id, f.id);
        } catch (e) {
          if (!(e instanceof ApiError && e.code === "not_pending")) throw e;
        }
      }
    },
    onSuccess: async () => {
      setError(null);
      await refresh();
    },
    onError,
  });
  const reread = useMutation({
    mutationFn: () => documentFactsApi.extract(doc.id),
    onSuccess: async () => {
      setError(null);
      toast.success(t("documentFacts.rereadStarted"));
      await refresh();
    },
    onError,
  });

  const grouped = FACT_KINDS.map((k) => ({ kind: k, items: facts.filter((f) => f.kind === k) })).filter((g) => g.items.length > 0);
  const link = "text-label-lg text-primary hover:underline disabled:opacity-60";

  return (
    <Modal
      title={t("documentFacts.title", { name: doc.fileName })}
      onClose={onClose}
      footer={
        <>
          {!readOnly && (
            <button type="button" className={link} disabled={reread.isPending || busy} onClick={() => reread.mutate()}>
              {t("documentFacts.reread")}
            </button>
          )}
          <button type="button" className={link} onClick={onOpenOriginal}>
            {t("documentFacts.openOriginal")}
          </button>
          {!readOnly && pendingPlain.length > 0 && (
            <button
              type="button"
              disabled={confirmAll.isPending}
              onClick={() => confirmAll.mutate()}
              className="rounded-radii-lg bg-primary px-4 py-2 text-label-lg text-onPrimary hover:opacity-90 disabled:opacity-60"
            >
              {t("documentFacts.confirmAll")}
            </button>
          )}
        </>
      }
    >
      <div className="flex flex-col gap-5 p-5">
        {query.isPending ? (
          <p className="text-body-md text-onSurfaceVariant">{t("documentFacts.loading")}</p>
        ) : query.isError || !data ? (
          <p className="text-body-md text-error">{t("documentFacts.loadFailed")}</p>
        ) : (
          <>
            {busy && <p className="text-body-md text-onSurfaceVariant">{t("documentFacts.reading")}</p>}
            {data.readStatus === "failed" && <p className="text-body-md text-error">{t("documentFacts.readFailed")}</p>}
            {data.pagesTruncated && <p className="text-body-sm text-onSurfaceVariant">{t("documentFacts.truncated")}</p>}
            {!busy && data.readStatus === "done" && facts.length === 0 && (
              <p className="text-body-md text-onSurfaceVariant">{t("documentFacts.none")}</p>
            )}
            {error && <p className="text-body-md text-error">{error}</p>}
            {grouped.map((g) => (
              <section key={g.kind} className="flex flex-col gap-2">
                <h3 className="text-label-lg text-onSurfaceVariant">{t(`documentFacts.kinds.${g.kind}`)}</h3>
                <ul className="flex flex-col gap-2">
                  {g.items.map((f) => (
                    <FactRow
                      key={f.id}
                      fact={f}
                      readOnly={readOnly}
                      recorded={f.recorded === true || recordedIds.has(f.id)}
                      busy={act.isPending || confirmAll.isPending}
                      onConfirm={() => act.mutate(() => documentFactsApi.confirm(doc.id, f.id))}
                      onDiscard={() => act.mutate(() => documentFactsApi.discard(doc.id, f.id))}
                      onCorrect={(value) =>
                        act.mutateAsync(() => documentFactsApi.correct(doc.id, f.id, value)).then(
                          () => true,
                          () => false,
                        )
                      }
                      onMedication={() => setMedicationFact(f)}
                      onView={() => f.excerptRefId && setPassageId(f.excerptRefId)}
                    />
                  ))}
                </ul>
              </section>
            ))}
          </>
        )}
      </div>
      {medicationFact && (
        <MedicationFactModal
          documentId={doc.id}
          fact={medicationFact}
          onClose={() => setMedicationFact(null)}
          onSaved={() => {
            const id = medicationFact.id;
            setMedicationFact(null);
            setError(null);
            setRecordedIds((prev) => new Set(prev).add(id));
            toast.success(t("documentFacts.medication.saved"));
            void refresh();
            void queryClient.invalidateQueries({ queryKey: ["patient", doc.patientId] });
            void queryClient.invalidateQueries({ queryKey: ["timeline", doc.patientId] });
          }}
        />
      )}
      {passageId && <DocumentPassageModal excerptId={passageId} onClose={() => setPassageId(null)} />}
    </Modal>
  );
}

function FactRow({
  fact,
  readOnly,
  recorded,
  busy,
  onConfirm,
  onDiscard,
  onCorrect,
  onMedication,
  onView,
}: {
  fact: DocumentFact;
  readOnly: boolean;
  recorded: boolean;
  busy: boolean;
  onConfirm: () => void;
  onDiscard: () => void;
  onCorrect: (value: string) => Promise<boolean>;
  onMedication: () => void;
  onView: () => void;
}) {
  const { t } = useTranslation();
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState("");
  const corrected = fact.status === "corrected" && fact.correctedValue !== null;
  const link = "text-label-md text-primary hover:underline disabled:opacity-60";
  // A pending medication fact goes through the medication form; once reviewed it keeps the plain correction.
  const viaForm = fact.kind === "medication" && fact.status === "pending" && !recorded;

  return (
    <li className="rounded-radii-lg border border-outlineVariant p-3">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div className="min-w-0 flex-1">
          {corrected ? (
            <p className="break-words text-body-md text-onSurface">
              {t("documentFacts.correctedFrom", { read: fact.value, corrected: fact.correctedValue })}
            </p>
          ) : (
            <p className="break-words text-body-md text-onSurface">{fact.value}</p>
          )}
          {fact.page !== null && <p className="text-body-sm text-onSurfaceVariant">{t("documentFacts.page", { page: fact.page })}</p>}
          {fact.quote && <p className="mt-1 break-words text-body-sm italic text-onSurfaceVariant">“{fact.quote}”</p>}
        </div>
        {fact.status === "confirmed" && <Badge label={t("documentFacts.status.confirmed")} tone="mint" />}
        {corrected && <Badge label={t("documentFacts.status.corrected")} tone="blue" />}
        {fact.status === "pending" && <Badge label={t("documentFacts.status.pending")} tone="lavender" />}
        {recorded && <Badge label={t("documentFacts.recorded")} tone="mint" />}
      </div>

      {editing ? (
        <form
          className="mt-2 flex flex-wrap items-center gap-2"
          onSubmit={(e) => {
            e.preventDefault();
            const v = draft.trim();
            if (!v) return;
            void onCorrect(v).then((ok) => ok && setEditing(false));
          }}
        >
          <input
            className={`${inputClass} flex-1`}
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            maxLength={200}
            aria-label={t("documentFacts.correctedValue")}
            autoFocus
          />
          <button type="submit" disabled={busy || !draft.trim()} className={link}>
            {t("common.save")}
          </button>
          <button type="button" onClick={() => setEditing(false)} className={link}>
            {t("common.cancel")}
          </button>
        </form>
      ) : (
        <div className="mt-2 flex flex-wrap items-center gap-3">
          {!readOnly && fact.status === "pending" && !recorded && (
            <button type="button" disabled={busy} onClick={viaForm ? onMedication : onConfirm} className={link}>
              {t("documentFacts.confirm")}
            </button>
          )}
          {!readOnly && (
            <button
              type="button"
              disabled={busy}
              onClick={() => {
                if (viaForm) return onMedication();
                setDraft(fact.correctedValue ?? fact.value);
                setEditing(true);
              }}
              className={link}
            >
              {t("documentFacts.correct")}
            </button>
          )}
          {!readOnly && (
            <button type="button" disabled={busy} onClick={onDiscard} className="text-label-md text-error hover:underline disabled:opacity-60">
              {t("documentFacts.discard")}
            </button>
          )}
          {!readOnly && fact.kind === "medication" && fact.status !== "pending" && !recorded && (
            <button type="button" disabled={busy} onClick={onMedication} className={link}>
              {t("documentFacts.medication.register")}
            </button>
          )}
          {fact.excerptRefId && (
            <button type="button" onClick={onView} className={link}>
              {t("documentFacts.viewInDocument")}
            </button>
          )}
        </div>
      )}
    </li>
  );
}
