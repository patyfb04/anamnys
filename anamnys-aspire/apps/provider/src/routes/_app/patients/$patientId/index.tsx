import { useEffect, useRef, useState, type ReactNode } from "react";
import { createFileRoute, Link, useNavigate, useRouterState } from "@tanstack/react-router";
import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import {
  Archive,
  ArchiveRestore,
  ArrowLeft,
  CalendarClock,
  CalendarDays,
  CalendarPlus,
  ChevronLeft,
  ChevronRight,
  FileText,
  FilePlus,
  FileSignature,
  FileUp,
  Mic,
  MoreHorizontal,
  Pencil,
  Trash2,
  UserRound,
} from "lucide-react";
import { patientsApi } from "@anamnys/shared/api/patients";
import { notesApi } from "@anamnys/shared/api/notes";
import Avatar from "@anamnys/shared/ui/Avatar";
import Badge from "@anamnys/shared/ui/Badge";
import DetailCard from "@/components/patients/detail/DetailCard";
import { DiagnosesCard, MedicationsCard, ObjectivesCard } from "@/components/patients/detail/ClinicalCards";
import ScalesCard from "@/components/scales/ScalesCard";
import AttendanceCard from "@/components/attendance/AttendanceCard";
import TimelineCard from "@/components/timeline/TimelineCard";
import DocumentsCard from "@/components/documents/DocumentsCard";
import DeclaredFactsCard from "@/components/externalDocuments/DeclaredFactsCard";
import ExternalDocumentsCard from "@/components/externalDocuments/ExternalDocumentsCard";
import NewDocumentModal from "@/components/documents/NewDocumentModal";
import UploadExternalDocumentModal from "@/components/externalDocuments/UploadExternalDocumentModal";
import PatientFormModal from "@/components/patients/PatientFormModal";
import ConfirmDialog from "@/components/patients/ConfirmDialog";
import ThemeFilter from "@/components/themes/ThemeFilter";
import NoteStatusBadge from "@/components/patients/NoteStatusBadge";
import PortalAccessBlock from "@/components/patients/detail/PortalAccessBlock";
import { ageFrom, formatDate, formatDay, formatTime } from "@/components/patients/format";

export const Route = createFileRoute("/_app/patients/$patientId/")({
  component: PatientDetailPage,
});

type Confirm = "archive" | "delete" | null;

// Patient record: header actions, contact, visits, clinical lists and recent notes. See
// design/specs/2026-10-01-patient-records-design.md §6.
function PatientDetailPage() {
  const { patientId } = Route.useParams();
  // Set by the create modal when it could not send the invitation it was asked to send.
  const inviteError = useRouterState({ select: (s) => s.location.state.inviteError });
  const { t, i18n } = useTranslation();
  const locale = i18n.language;
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [editing, setEditing] = useState(false);
  const [confirm, setConfirm] = useState<Confirm>(null);
  const [actionError, setActionError] = useState<string | null>(null);
  const [creatingDocument, setCreatingDocument] = useState(false);
  const [uploading, setUploading] = useState(false);

  const query = useQuery({ queryKey: ["patient", patientId], queryFn: () => patientsApi.get(patientId) });
  const patient = query.data;

  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: ["patient", patientId] });
    await queryClient.invalidateQueries({ queryKey: ["patients"] });
    await queryClient.invalidateQueries({ queryKey: ["timeline", patientId] });
    await queryClient.invalidateQueries({ queryKey: ["attendance", patientId] });
  };

  const archive = useMutation({
    mutationFn: (archived: boolean) => (archived ? patientsApi.archive(patientId) : patientsApi.unarchive(patientId)),
    onSuccess: async () => {
      setConfirm(null);
      await refresh();
    },
    onError: () => setActionError(t("patients.detail.actionFailed")),
  });

  const remove = useMutation({
    mutationFn: () => patientsApi.remove(patientId),
    onSuccess: async () => {
      setConfirm(null);
      queryClient.removeQueries({ queryKey: ["patient", patientId] });
      await queryClient.invalidateQueries({ queryKey: ["patients"] });
      navigate({ to: "/patients" });
    },
    onError: (e: Error) => {
      setConfirm(null);
      setActionError(e.message);
    },
  });

  if (query.isPending) {
    return <div className="p-4 md:p-8 text-body-md text-onSurfaceVariant">{t("patients.detail.loading")}</div>;
  }
  if (query.isError || !patient) {
    return (
      <div className="p-4 md:p-8 flex flex-col items-start gap-3">
        <p className="text-error">{t("patients.detail.failedToLoad")}</p>
        <BackLink />
      </div>
    );
  }

  const fullName = `${patient.firstName} ${patient.lastName}`;
  const age = ageFrom(patient.dateOfBirth);
  const archived = patient.archivedAt !== null;
  const actionButton =
    "inline-flex items-center gap-2 px-4 py-2 border border-outlineVariant rounded-radii-md text-label-lg text-onSurface bg-surfaceContainerLowest hover:bg-pastelLavender transition-colors";

  return (
    <div className="p-4 md:p-8 flex flex-col gap-6">
      <BackLink />

      <header className="flex flex-col gap-4 lg:flex-row lg:items-center lg:justify-between">
        <div className="flex items-center gap-4 min-w-0">
          <Avatar name={fullName} size={72} />
          <div className="min-w-0">
            <div className="flex flex-wrap items-center gap-2">
              <h1 className="text-headline-lg text-onSurface truncate">{fullName}</h1>
              <Badge
                label={archived ? t("patients.detail.archived") : t("patients.detail.active")}
                tone={archived ? "neutral" : "mint"}
              />
            </div>
            <p className="text-body-md text-onSurfaceVariant mt-1">
              {patient.dateOfBirth
                ? t("patients.detail.born", { date: formatDay(patient.dateOfBirth, locale), age })
                : t("patients.detail.noBirthDate")}
            </p>
          </div>
        </div>
        <div className="flex flex-wrap items-center gap-2">
          {!archived && (
            <Link
              to="/live-session"
              search={{ patientId }}
              className="inline-flex items-center gap-2 px-4 py-2 rounded-radii-md text-label-lg text-onPrimary bg-primary hover:opacity-90 transition-opacity"
            >
              <Mic size={18} />
              {t("patients.detail.startSession")}
            </Link>
          )}
          {!archived && (
            <Link to="/calendar" search={{ patientId }} className={actionButton}>
              <CalendarPlus size={18} />
              {t("patients.detail.schedule")}
            </Link>
          )}
          {!archived && (
            <Link to="/note-editor/new" search={{ patientId }} className={actionButton}>
              <FilePlus size={18} />
              {t("patients.detail.newNote")}
            </Link>
          )}
          {!archived && (
            <button type="button" onClick={() => setCreatingDocument(true)} className={actionButton}>
              <FileSignature size={18} />
              {t("patients.detail.newDocument")}
            </button>
          )}
          {!archived && (
            <button type="button" onClick={() => setUploading(true)} className={actionButton}>
              <FileUp size={18} />
              {t("externalDocuments.upload")}
            </button>
          )}
          <MoreActions label={t("patients.detail.moreActions")}>
            <MenuItem icon={<Pencil size={18} />} label={t("common.edit")} onSelect={() => setEditing(true)} />
            {archived ? (
              <MenuItem
                icon={<ArchiveRestore size={18} />}
                label={t("patients.detail.unarchive")}
                onSelect={() => archive.mutate(false)}
                disabled={archive.isPending}
              />
            ) : (
              <MenuItem icon={<Archive size={18} />} label={t("patients.detail.archive")} onSelect={() => setConfirm("archive")} />
            )}
            {patient.canDelete && (
              <MenuItem
                icon={<Trash2 size={18} />}
                label={t("patients.detail.delete")}
                onSelect={() => setConfirm("delete")}
                destructive
              />
            )}
          </MoreActions>
        </div>
      </header>

      {actionError && <p className="text-body-md text-error">{actionError}</p>}
      {creatingDocument && <NewDocumentModal patientId={patientId} onClose={() => setCreatingDocument(false)} />}
      {uploading && <UploadExternalDocumentModal patientId={patientId} onClose={() => setUploading(false)} />}

      <div className="grid gap-6 lg:grid-cols-3">
        <DetailCard title={t("patients.detail.contact")} icon={<UserRound size={20} />}>
          <dl className="flex flex-col gap-3">
            <Field label={t("patients.detail.contactEmail")} value={patient.contactEmail ?? "—"} />
            <Field label={t("patients.detail.phone")} value={patient.phone ?? "—"} />
          </dl>
          <div className="mt-4">
            <PortalAccessBlock patient={patient} onChanged={refresh} initialError={inviteError ?? null} />
          </div>
        </DetailCard>

        <DetailCard title={t("patients.detail.visits")} icon={<CalendarClock size={20} />}>
          <dl className="flex flex-col gap-3">
            <Field label={t("patients.list.columns.lastVisit")} value={formatDate(patient.lastVisit, locale)} />
            <Field
              label={t("patients.list.columns.nextVisit")}
              value={
                patient.nextAppointmentAt
                  ? `${formatDate(patient.nextAppointmentAt, locale)} ${formatTime(patient.nextAppointmentAt, locale)}`
                  : "—"
              }
            />
            <div>
              <dt className="text-label-md text-onSurfaceVariant mb-1">{t("patients.list.columns.status")}</dt>
              <dd>
                <NoteStatusBadge status={patient.noteStatus} />
              </dd>
            </div>
          </dl>
        </DetailCard>

        <DetailCard title={t("patients.detail.recentNotes")} icon={<FileText size={20} />}>
          <RecentNotes patientId={patientId} />
        </DetailCard>
      </div>

      {/* Clinical lists carry per-item actions, so they get wider columns than the summary row. */}
      <div className="grid gap-6 lg:grid-cols-2 2xl:grid-cols-3">
        <DiagnosesCard patientId={patientId} items={patient.diagnoses} readOnly={archived} onChanged={refresh} />
        <MedicationsCard patientId={patientId} items={patient.medications} readOnly={archived} onChanged={refresh} />
        <ObjectivesCard patientId={patientId} items={patient.objectives} readOnly={archived} onChanged={refresh} />
      </div>

      {/* Full width: each instrument's series gets its own column inside the card. */}
      <ScalesCard patientId={patientId} readOnly={archived} />

      <AttendanceCard patientId={patientId} />

      <TimelineCard patientId={patientId} />

      <DocumentsCard patientId={patientId} readOnly={archived} />

      <ExternalDocumentsCard patientId={patientId} readOnly={archived} />

      <DeclaredFactsCard patientId={patientId} />

      {editing && (
        <PatientFormModal mode={{ kind: "edit", patient }} onClose={() => setEditing(false)} onSaved={() => setEditing(false)} />
      )}

      <ConfirmDialog
        open={confirm === "archive"}
        onOpenChange={(open) => !open && setConfirm(null)}
        title={t("patients.detail.archiveTitle")}
        description={t("patients.detail.archiveBody", { name: fullName })}
        confirmLabel={t("patients.detail.archive")}
        pending={archive.isPending}
        onConfirm={() => archive.mutate(true)}
      />
      <ConfirmDialog
        open={confirm === "delete"}
        onOpenChange={(open) => !open && setConfirm(null)}
        title={t("patients.detail.deleteTitle")}
        description={t("patients.detail.deleteBody", { name: fullName })}
        confirmLabel={t("patients.detail.delete")}
        destructive
        pending={remove.isPending}
        onConfirm={() => remove.mutate()}
      />
    </div>
  );
}

function BackLink() {
  const { t } = useTranslation();
  return (
    <Link to="/patients" className="inline-flex items-center gap-1 self-start text-label-lg text-primary hover:underline">
      <ArrowLeft size={16} />
      {t("patients.list.title")}
    </Link>
  );
}

function Field({ label, value }: { label: string; value: string }) {
  return (
    <div>
      <dt className="text-label-md text-onSurfaceVariant">{label}</dt>
      <dd className="text-body-md text-onSurface break-words">{value}</dd>
    </div>
  );
}

const RECENT_NOTES_PAGE_SIZE = 3;

// The patient's notes, newest first, three per page so the card keeps its height as the history grows.
function RecentNotes({ patientId }: { patientId: string }) {
  const { t, i18n } = useTranslation();
  const [page, setPage] = useState(1);
  const [termId, setTermId] = useState("");
  const query = useQuery({
    queryKey: ["notes", "patient", patientId, "recent", page, termId],
    queryFn: () => notesApi.search({ patientId, termId: termId || undefined, page, pageSize: RECENT_NOTES_PAGE_SIZE }),
    placeholderData: keepPreviousData,
  });
  if (!query.data) {
    return query.isError ? <p className="text-body-md text-error">{t("patients.notes.failedToLoad")}</p> : null;
  }
  const notes = query.data.items;
  const themeFilter = (
    <ThemeFilter
      value={termId}
      onChange={(id) => {
        setTermId(id);
        setPage(1);
      }}
    />
  );
  if (notes.length === 0 && page === 1) {
    if (termId) {
      return (
        <div className="flex flex-col gap-3">
          {themeFilter}
          <p className="text-body-md text-onSurfaceVariant">{t("themes.filter.noResults")}</p>
        </div>
      );
    }
    return <p className="text-body-md text-onSurfaceVariant">{t("patients.detail.noNotes")}</p>;
  }
  const pagerButton =
    "p-1.5 rounded-radii-md text-onSurfaceVariant hover:bg-pastelLavender disabled:opacity-40 disabled:hover:bg-transparent transition-colors";
  return (
    <div className="flex flex-col gap-3">
    {themeFilter}
    <ul className="flex flex-col gap-2">
      {notes.map((note) => {
        const signed = note.status === "Signed" || note.status === "Exported";
        return (
          <li
            key={note.id}
            className={`flex flex-wrap items-center justify-between gap-3 rounded-radii-lg border p-3 ${
              signed ? "border-outlineVariant" : "border-transparent bg-surfaceContainerLow"
            }`}
          >
            <div className="flex items-center gap-3 min-w-0">
              <span className="flex h-10 w-10 shrink-0 items-center justify-center rounded-radii-md bg-surfaceContainerHighest text-primary">
                <CalendarDays size={18} aria-hidden />
              </span>
              <div className="min-w-0">
                <p className="truncate text-label-lg text-onSurface">{note.format}</p>
                <p className="text-body-sm text-onSurfaceVariant">{formatDate(note.createdAt, i18n.language)}</p>
                {note.snippet && (
                  <p className="mt-1 text-body-sm italic text-onSurfaceVariant">{note.snippet}</p>
                )}
              </div>
            </div>
            <div className="flex items-center gap-2">
              <NoteStatusBadge status={signed ? "signed" : "pending"} />
              <Link
                to="/notes/$noteId"
                params={{ noteId: note.id }}
                search={{ excerpt: termId ? (note.excerptId ?? undefined) : undefined }}
                className={`rounded-radii-md px-3 py-1.5 text-label-md ${
                  signed ? "text-onSurfaceVariant hover:text-primary" : "border border-primary text-primary hover:bg-primaryFixed"
                }`}
              >
                {signed ? t("patients.detail.viewNote") : t("patients.detail.continueNote")}
              </Link>
            </div>
          </li>
        );
      })}
    </ul>
      {(page > 1 || query.data.hasMore) && (
        <div className="flex items-center justify-between">
          <p className="text-body-sm text-onSurfaceVariant">{t("patients.detail.notesPage", { page })}</p>
          <div className="flex gap-1">
            <button
              type="button"
              className={pagerButton}
              disabled={page <= 1}
              onClick={() => setPage((p) => p - 1)}
              aria-label={t("patients.list.previousPage")}
            >
              <ChevronLeft size={20} />
            </button>
            <button
              type="button"
              className={pagerButton}
              disabled={!query.data.hasMore}
              onClick={() => setPage((p) => p + 1)}
              aria-label={t("patients.list.nextPage")}
            >
              <ChevronRight size={20} />
            </button>
          </div>
        </div>
      )}
    </div>
  );
}

// Secondary record actions (edit, archive, delete) behind a "⋯" button; Escape or a click outside closes it.
function MoreActions({ label, children }: { label: string; children: ReactNode }) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (!open) return;
    const onPointer = (e: PointerEvent) => {
      if (!ref.current?.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") setOpen(false);
    };
    document.addEventListener("pointerdown", onPointer);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("pointerdown", onPointer);
      document.removeEventListener("keydown", onKey);
    };
  }, [open]);
  return (
    <div ref={ref} className="relative">
      <button
        type="button"
        aria-label={label}
        aria-haspopup="menu"
        aria-expanded={open}
        onClick={() => setOpen((v) => !v)}
        className="inline-flex items-center justify-center p-2 border border-outlineVariant rounded-radii-md text-onSurface bg-surfaceContainerLowest hover:bg-pastelLavender"
      >
        <MoreHorizontal size={18} />
      </button>
      {open && (
        <div
          role="menu"
          onClick={() => setOpen(false)}
          className="absolute right-0 top-full z-50 mt-1 min-w-48 rounded-radii-md border border-outlineVariant bg-surfaceContainerLowest py-1 shadow-lg"
        >
          {children}
        </div>
      )}
    </div>
  );
}

function MenuItem(props: { icon: ReactNode; label: string; onSelect: () => void; disabled?: boolean; destructive?: boolean }) {
  return (
    <button
      type="button"
      role="menuitem"
      disabled={props.disabled}
      onClick={props.onSelect}
      className={`flex w-full items-center gap-2 px-4 py-2 text-left text-label-lg hover:bg-pastelLavender disabled:opacity-60 ${
        props.destructive ? "text-error" : "text-onSurface"
      }`}
    >
      {props.icon}
      {props.label}
    </button>
  );
}
