import { useRef, useState, type ReactNode } from "react";
import { createFileRoute, Link, useNavigate } from "@tanstack/react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { AlertCircle, ArrowLeft, Check, ClipboardList, FileText, Loader2, PenLine, Trash2 } from "lucide-react";
import { intakeApi } from "@anamnys/shared/api/intake";
import { patientsApi } from "@anamnys/shared/api/patients";
import type { IntakeAnswer, IntakeDetail, IntakeQuestion } from "@anamnys/shared/lib/types";
import Badge from "@anamnys/shared/ui/Badge";
import { toast } from "@anamnys/shared/ui/Toaster";
import DetailCard from "@/components/patients/detail/DetailCard";
import { CardFooterContext, goToCard, OPEN_CARD_EVENT } from "@/components/patients/detail/cardAnchors";
import ConfirmDialog from "@/components/patients/ConfirmDialog";
import BackToTop from "@/components/analysis/BackToTop";
import IntakeQuestionField from "@/components/intake/IntakeQuestionField";
import IntakeTransfersPanel from "@/components/intake/IntakeTransfersPanel";
import { formatDate } from "@/components/patients/format";
import { errorCode, intakeKey, intakesKey, questionIdsOf, useDiscardIntake } from "@/components/intake/intakeModel";

export const Route = createFileRoute("/_app/patients/$patientId/intake/$intakeId")({
  // ?transfers=1 (after signing the Anamnese note) scrolls the "Levar para o prontuário" panel into view.
  validateSearch: (search: Record<string, unknown>): { transfers?: 1 } =>
    search.transfers === 1 || search.transfers === "1" ? { transfers: 1 } : {},
  component: IntakePage,
});

const TOP_ID = "intake-top";
const cardId = (sectionKey: string) => `intake-${sectionKey}`;

type SaveState = "saving" | "saved" | "error";

class UnsavedAnswers extends Error {}

// The intake (anamnese) of design/specs/2026-11-12-structured-intake-design.md §5: one collapsible block per template
// section, answers saved on blur, "Gerar nota para revisão" opening the note in the Note Editor, "Editar respostas"
// while the note is unsigned and, once signed, the read-only answers with "Levar para o prontuário".
function IntakePage() {
  const { t } = useTranslation();
  const { patientId, intakeId } = Route.useParams();
  const { transfers: showTransfers } = Route.useSearch();
  const query = useQuery({ queryKey: intakeKey(intakeId), queryFn: () => intakeApi.get(intakeId) });
  const patient = useQuery({ queryKey: ["patient", patientId], queryFn: () => patientsApi.get(patientId) });

  const back = (
    <Link
      to="/patients/$patientId"
      params={{ patientId }}
      search={{ tab: "avaliacao" }}
      className="inline-flex items-center gap-1 self-start text-label-lg text-primary hover:underline"
    >
      <ArrowLeft size={16} />
      {t("intake.page.backToPatient")}
    </Link>
  );

  if (query.isPending) {
    return <div className="flex flex-col gap-6 p-4 md:p-8">{back}</div>;
  }
  if (query.isError || query.data.patientId !== patientId) {
    return (
      <div className="flex flex-col gap-3 p-4 md:p-8">
        {back}
        <p className="text-body-md text-error">{t("intake.page.notFound")}</p>
      </div>
    );
  }
  const intake = query.data;
  const archived = !!patient.data?.archivedAt;
  // Until the patient is known the answers stay read-only, so an archived patient's intake never flashes editable.
  const patientKnown = !!patient.data;

  return (
    <div className="flex flex-col gap-6 p-4 md:p-8">
      {back}
      {/* Remounts with fresh answers when the note is created or removed, or the intake is signed. */}
      <IntakeForm
        key={`${intake.id}:${intake.noteId ?? ""}:${intake.status}`}
        intake={intake}
        patientName={patient.data ? `${patient.data.firstName} ${patient.data.lastName}` : ""}
        archived={archived}
        patientKnown={patientKnown}
        focusTransfers={!!showTransfers}
        onStale={() => void query.refetch()}
      />
    </div>
  );
}

function IntakeForm(props: {
  intake: IntakeDetail;
  patientName: string;
  archived: boolean;
  patientKnown: boolean;
  focusTransfers: boolean;
  onStale: () => void;
}) {
  const { intake, patientName, archived, patientKnown, focusTransfers, onStale } = props;
  const { t, i18n } = useTranslation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const patientId = intake.patientId;

  const initial = () => Object.fromEntries(intake.answers.map((a) => [a.questionId, a.answer]));
  const [values, setValues] = useState<Record<string, string>>(initial);
  const savedRef = useRef<Record<string, string>>(values);
  const chainRef = useRef<Promise<void>>(Promise.resolve());
  const inflightRef = useRef<Record<string, number>>({});
  // The last value queued for each question and not yet answered by the server: a field edited A→B→A while B is still
  // on its way is compared with B (and A is sent again), never with the older stored A.
  const pendingRef = useRef<Record<string, string>>({});
  const failedRef = useRef<Record<string, boolean>>({});
  const valuesRef = useRef(values);
  valuesRef.current = values;
  const [sectionState, setSectionState] = useState<Record<string, SaveState>>({});
  const [invalid, setInvalid] = useState<Set<string>>(new Set());
  const [missing, setMissing] = useState<Set<string>>(new Set());
  const [confirm, setConfirm] = useState<"unlock" | "discard" | null>(null);

  const draft = intake.status === "draft";
  const editable = draft && !intake.noteId && patientKnown && !archived;
  const sectionOf = new Map<string, string>();
  for (const s of intake.sections) for (const q of s.questions) sectionOf.set(q.id, s.key);

  // Saves are queued one after another (one chain per intake), so a later answer never lands before an earlier one; a
  // block shows "Salvando…" while any of its saves runs, then "Salvo" or "Não salvo". A failed save keeps what is in
  // the field, so blurring it again retries.
  const save = (question: IntakeQuestion, sectionKey: string, raw: string) => {
    const value = raw.trim();
    const last = pendingRef.current[question.id] ?? savedRef.current[question.id] ?? "";
    if (!editable || value === last) return;
    pendingRef.current[question.id] = value;
    const settle = () => {
      if (pendingRef.current[question.id] === value) delete pendingRef.current[question.id];
    };
    inflightRef.current[sectionKey] = (inflightRef.current[sectionKey] ?? 0) + 1;
    setSectionState((s) => ({ ...s, [sectionKey]: "saving" }));
    chainRef.current = chainRef.current
      .then(() => intakeApi.saveAnswers(intake.id, [{ questionId: question.id, answer: value || null }]))
      .then((answers: IntakeAnswer[]) => {
        const stored = Object.fromEntries(answers.map((a) => [a.questionId, a.answer]));
        savedRef.current = stored;
        settle();
        const normalized = stored[question.id] ?? "";
        // Show the stored form ("7,5" → "7.5") unless the field changed again meanwhile.
        setValues((v) => (v[question.id]?.trim() === value ? { ...v, [question.id]: normalized } : v));
        setInvalid((s) => (s.has(question.id) ? new Set([...s].filter((id) => id !== question.id)) : s));
        if (normalized) setMissing((s) => (s.has(question.id) ? new Set([...s].filter((id) => id !== question.id)) : s));
      })
      .catch((e: unknown) => {
        settle();
        failedRef.current[sectionKey] = true;
        const code = errorCode(e);
        if (code === "invalid_answer") {
          setInvalid((s) => new Set([...s, ...questionIdsOf(e)]));
        } else if (code === "answers_locked" || code === "not_draft" || code === "patient_archived") {
          toast.error(t("intake.page.locked"));
          onStale();
        } else {
          toast.error(t("intake.page.saveFailed"));
        }
      })
      .finally(() => {
        inflightRef.current[sectionKey] -= 1;
        if (inflightRef.current[sectionKey] > 0) return;
        const failed = !!failedRef.current[sectionKey];
        failedRef.current[sectionKey] = false;
        setSectionState((s) => ({ ...s, [sectionKey]: failed ? "error" : "saved" }));
      });
  };

  const openNote = (noteId: string) => navigate({ to: "/notes/$noteId", params: { noteId } });

  const generate = useMutation({
    mutationFn: async () => {
      // Answers still being saved (the click blurred the last field) go first; an answer that could not be saved
      // stops the note, so it never silently leaves out what is on screen.
      await chainRef.current;
      const unsaved = Object.entries(valuesRef.current).some(([id, v]) => v.trim() !== (savedRef.current[id] ?? ""));
      if (unsaved) throw new UnsavedAnswers();
      return intakeApi.generateNote(intake.id);
    },
    onSuccess: (noteId) => {
      void queryClient.invalidateQueries({ queryKey: intakeKey(intake.id) });
      void queryClient.invalidateQueries({ queryKey: intakesKey(patientId) });
      void queryClient.invalidateQueries({ queryKey: ["notes"] });
      void openNote(noteId);
    },
    onError: (e) => {
      const code = errorCode(e);
      if (e instanceof UnsavedAnswers) toast.error(t("intake.page.unsavedAnswers"));
      else if (code === "required_missing") {
        const ids = questionIdsOf(e);
        setMissing(new Set(ids));
        const sections = [...new Set(ids.map((id) => sectionOf.get(id)).filter((k): k is string => !!k))];
        for (const key of sections) window.dispatchEvent(new CustomEvent(OPEN_CARD_EVENT, { detail: cardId(key) }));
        if (sections[0]) goToCard(cardId(sections[0]));
        toast.error(t("intake.page.requiredMissing", { count: ids.length }));
      } else if (code === "not_draft" || code === "patient_archived") {
        toast.error(t("intake.page.locked"));
        onStale();
      } else toast.error(t("intake.page.generateFailed"));
    },
  });

  const unlock = useMutation({
    mutationFn: () => intakeApi.unlock(intake.id),
    onSuccess: (detail) => {
      setConfirm(null);
      if (intake.noteId) queryClient.removeQueries({ queryKey: ["note", intake.noteId] });
      void queryClient.invalidateQueries({ queryKey: ["notes"] });
      void queryClient.invalidateQueries({ queryKey: intakesKey(patientId) });
      queryClient.setQueryData(intakeKey(intake.id), detail);
    },
    onError: () => {
      setConfirm(null);
      toast.error(t("intake.page.unlockFailed"));
      onStale();
    },
  });

  const discard = useDiscardIntake(patientId, () => {
    setConfirm(null);
    void navigate({ to: "/patients/$patientId", params: { patientId }, search: { tab: "avaliacao" } });
  });

  const statusText = (key: string) => {
    const state = sectionState[key];
    if (!state) return null;
    return (
      <span
        aria-live="polite"
        className={`inline-flex items-center gap-1 text-label-md ${state === "error" ? "text-error" : "text-onSurfaceVariant"}`}
      >
        {state === "saving" && <Loader2 size={14} className="animate-spin" aria-hidden />}
        {state === "saved" && <Check size={14} aria-hidden />}
        {state === "error" && <AlertCircle size={14} aria-hidden />}
        {t(`intake.page.${state === "error" ? "saveFailed" : state}`)}
      </span>
    );
  };

  const hasRequired = intake.sections.some((s) => s.questions.some((q) => q.required));

  return (
    <>
      <header id={TOP_ID} className="flex scroll-mt-24 flex-col gap-2">
        <div className="flex flex-wrap items-center gap-2">
          <h1 className="text-headline-lg text-onSurface">{t("intake.page.title")}</h1>
          <Badge label={t(`intake.status.${intake.status}`)} tone={draft ? "blue" : "mint"} />
        </div>
        <p className="text-body-md text-onSurfaceVariant">
          {[patientName, formatDate(intake.createdAt, i18n.language), t("intake.page.template", { name: intake.template.name, version: intake.template.version })]
            .filter(Boolean)
            .join(" · ")}
        </p>
      </header>

      {!draft && (
        <Banner>
          <span>{t("intake.page.signedReadOnly", { date: formatDate(intake.signedAt, i18n.language) })}</span>
          {intake.noteId && <NoteLink noteId={intake.noteId} label={t("intake.page.viewSignedNote")} />}
        </Banner>
      )}
      {draft && intake.noteId && (
        <Banner>
          <span>{t("intake.page.noteExists")}</span>
          <div className="flex flex-wrap gap-2">
            {patientKnown && !archived && (
              <button
                type="button"
                onClick={() => setConfirm("unlock")}
                className="inline-flex items-center gap-1 rounded-radii-md border border-outlineVariant bg-surfaceContainerLowest px-3 py-1.5 text-label-md text-onSurface hover:bg-pastelLavender"
              >
                <PenLine size={14} aria-hidden />
                {t("intake.page.editAnswers")}
              </button>
            )}
            {patientKnown && !archived && (
              <button
                type="button"
                onClick={() => setConfirm("discard")}
                className="inline-flex items-center gap-1 rounded-radii-md px-3 py-1.5 text-label-md text-error hover:bg-errorContainer"
              >
                <Trash2 size={14} aria-hidden />
                {t("intake.card.discardDraft")}
              </button>
            )}
            <NoteLink noteId={intake.noteId} label={t("intake.page.openNote")} primary />
          </div>
        </Banner>
      )}
      {draft && patientKnown && archived && <Banner>{t("intake.page.archivedReadOnly")}</Banner>}

      {!draft && <IntakeTransfersPanel intakeId={intake.id} patientId={patientId} readOnly={archived} focus={focusTransfers} />}

      <nav aria-label={t("intake.page.navLabel")} className="grid grid-cols-2 gap-2 sm:grid-cols-3 lg:grid-cols-6">
        {intake.sections.map((s) => (
          <button
            key={s.key}
            type="button"
            onClick={() => goToCard(cardId(s.key))}
            className="h-full rounded-radii-md border border-outlineVariant bg-surfaceContainerLowest px-2 py-2 text-center text-label-md leading-tight text-primary shadow-sm hover:bg-pastelLavender"
          >
            {s.title}
          </button>
        ))}
      </nav>

      {editable && hasRequired && <p className="text-body-sm text-onSurfaceVariant">{t("intake.page.requiredLegend")}</p>}

      <CardFooterContext.Provider value={<BackToTop targetId={TOP_ID} />}>
        {intake.sections.map((s) => (
          <DetailCard
            key={s.key}
            collapsibleId={cardId(s.key)}
            defaultOpen
            title={s.title}
            icon={<ClipboardList size={20} />}
            action={editable ? statusText(s.key) : undefined}
          >
            <div className="flex flex-col gap-5">
              {s.questions.map((q) => (
                <IntakeQuestionField
                  key={q.id}
                  question={q}
                  value={values[q.id] ?? ""}
                  readOnly={!editable}
                  invalid={invalid.has(q.id)}
                  missing={missing.has(q.id)}
                  onChange={(v) => setValues((old) => ({ ...old, [q.id]: v }))}
                  onCommit={(v) => save(q, s.key, v)}
                />
              ))}
            </div>
          </DetailCard>
        ))}
      </CardFooterContext.Provider>

      {editable && (
        <footer className="flex flex-col gap-3 rounded-radii-xl border border-outlineVariant bg-surfaceContainerLowest p-5 shadow-sm sm:flex-row sm:items-center sm:justify-between">
          <p className="text-body-sm text-onSurfaceVariant">{t("intake.page.generateHint")}</p>
          <div className="flex flex-wrap justify-end gap-2">
            <button
              type="button"
              onClick={() => setConfirm("discard")}
              className="inline-flex items-center gap-1 rounded-radii-md px-3 py-2 text-label-lg text-error hover:bg-errorContainer"
            >
              <Trash2 size={16} aria-hidden />
              {t("intake.card.discardDraft")}
            </button>
            <button
              type="button"
              disabled={generate.isPending}
              onClick={() => generate.mutate()}
              className="inline-flex items-center gap-2 rounded-radii-md bg-primary px-5 py-2 text-label-lg text-onPrimary hover:opacity-90 disabled:opacity-60"
            >
              {generate.isPending ? <Loader2 size={16} className="animate-spin" aria-hidden /> : <FileText size={16} aria-hidden />}
              {t("intake.page.generate")}
            </button>
          </div>
        </footer>
      )}

      <ConfirmDialog
        open={confirm === "unlock"}
        onOpenChange={(open) => !open && setConfirm(null)}
        title={t("intake.page.unlockTitle")}
        description={t("intake.page.unlockBody")}
        confirmLabel={t("intake.page.editAnswers")}
        destructive
        pending={unlock.isPending}
        onConfirm={() => unlock.mutate()}
      />
      <ConfirmDialog
        open={confirm === "discard"}
        onOpenChange={(open) => !open && setConfirm(null)}
        title={t("intake.discard.title")}
        description={t("intake.discard.body")}
        confirmLabel={t("intake.discard.confirm")}
        destructive
        pending={discard.isPending}
        onConfirm={() => discard.mutate(intake.id)}
      />
    </>
  );
}

function Banner({ children }: { children: ReactNode }) {
  return (
    <div className="flex flex-col gap-3 rounded-radii-lg bg-surfaceContainerLow p-4 text-body-md text-onSurface sm:flex-row sm:items-center sm:justify-between">
      {children}
    </div>
  );
}

function NoteLink({ noteId, label, primary }: { noteId: string; label: string; primary?: boolean }) {
  return (
    <Link
      to="/notes/$noteId"
      params={{ noteId }}
      className={`inline-flex shrink-0 items-center gap-1 rounded-radii-md px-3 py-1.5 text-label-md ${
        primary ? "bg-primary text-onPrimary hover:opacity-90" : "border border-primary text-primary hover:bg-primaryFixed"
      }`}
    >
      <FileText size={14} aria-hidden />
      {label}
    </Link>
  );
}
