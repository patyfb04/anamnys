import { useState } from "react";
import { createFileRoute, Link, useNavigate } from "@tanstack/react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Archive, ArchiveRestore, ArrowLeft, CalendarClock, FileText, Pencil, Trash2, UserRound } from "lucide-react";
import { patientsApi } from "@anamnys/shared/api/patients";
import Avatar from "@anamnys/shared/ui/Avatar";
import Badge from "@anamnys/shared/ui/Badge";
import type { NoteSummary } from "@anamnys/shared/lib/types";
import DetailCard from "@/components/patients/detail/DetailCard";
import { DiagnosesCard, MedicationsCard, ObjectivesCard } from "@/components/patients/detail/ClinicalCards";
import PatientFormModal from "@/components/patients/PatientFormModal";
import ConfirmDialog from "@/components/patients/ConfirmDialog";
import NoteStatusBadge from "@/components/patients/NoteStatusBadge";
import { ageFrom, formatDate, formatDay, formatTime } from "@/components/patients/format";

export const Route = createFileRoute("/_app/patients/$patientId/")({
  component: PatientDetailPage,
});

type Confirm = "archive" | "delete" | null;

// Patient record: header actions, contact, visits, clinical lists and recent notes. See
// design/specs/2026-10-01-patient-records-design.md §6.
function PatientDetailPage() {
  const { patientId } = Route.useParams();
  const { t, i18n } = useTranslation();
  const locale = i18n.language;
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const [editing, setEditing] = useState(false);
  const [confirm, setConfirm] = useState<Confirm>(null);
  const [actionError, setActionError] = useState<string | null>(null);

  const query = useQuery({ queryKey: ["patient", patientId], queryFn: () => patientsApi.get(patientId) });
  const patient = query.data;

  const refresh = async () => {
    await queryClient.invalidateQueries({ queryKey: ["patient", patientId] });
    await queryClient.invalidateQueries({ queryKey: ["patients"] });
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
    "inline-flex items-center gap-2 px-4 py-2 border border-outlineVariant rounded-radii-md text-label-lg text-onSurface bg-surfaceContainerLowest hover:bg-surfaceContainerLow transition-colors";

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
        <div className="flex flex-wrap gap-2">
          <button type="button" className={actionButton} onClick={() => setEditing(true)}>
            <Pencil size={18} />
            {t("common.edit")}
          </button>
          {archived ? (
            <button type="button" className={actionButton} onClick={() => archive.mutate(false)} disabled={archive.isPending}>
              <ArchiveRestore size={18} />
              {t("patients.detail.unarchive")}
            </button>
          ) : (
            <button type="button" className={actionButton} onClick={() => setConfirm("archive")}>
              <Archive size={18} />
              {t("patients.detail.archive")}
            </button>
          )}
          {patient.canDelete && (
            <button
              type="button"
              className={`${actionButton} text-error border-error/40 hover:bg-errorContainer`}
              onClick={() => setConfirm("delete")}
            >
              <Trash2 size={18} />
              {t("patients.detail.delete")}
            </button>
          )}
        </div>
      </header>

      {actionError && <p className="text-body-md text-error">{actionError}</p>}

      <div className="grid gap-6 lg:grid-cols-3">
        <DetailCard title={t("patients.detail.contact")} icon={<UserRound size={20} />}>
          <dl className="flex flex-col gap-3">
            <Field label={t("patients.detail.email")} value={patient.email ?? t("patients.list.noPortal")} />
            <Field label={t("patients.detail.phone")} value={patient.phone ?? "—"} />
            <p className="text-label-md text-outline">
              {patient.hasPortalAccount ? t("patients.detail.portalLinked") : t("patients.detail.portalNotLinked")}
            </p>
          </dl>
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
          <RecentNotes notes={patient.recentNotes} />
        </DetailCard>
      </div>

      {/* Clinical lists carry per-item actions, so they get wider columns than the summary row. */}
      <div className="grid gap-6 lg:grid-cols-2 2xl:grid-cols-3">
        <DiagnosesCard patientId={patientId} items={patient.diagnoses} readOnly={archived} onChanged={refresh} />
        <MedicationsCard patientId={patientId} items={patient.medications} readOnly={archived} onChanged={refresh} />
        <ObjectivesCard patientId={patientId} items={patient.objectives} readOnly={archived} onChanged={refresh} />
      </div>

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

function RecentNotes({ notes }: { notes: NoteSummary[] }) {
  const { t, i18n } = useTranslation();
  if (notes.length === 0) return <p className="text-body-md text-onSurfaceVariant">{t("patients.detail.noNotes")}</p>;
  return (
    <ul className="flex flex-col gap-2">
      {notes.map((note) => (
        <li key={note.id}>
          <Link
            to="/notes/$noteId"
            params={{ noteId: note.id }}
            className="flex items-center justify-between gap-2 p-2 -mx-2 rounded-radii-md hover:bg-surfaceContainerLow"
          >
            <span className="text-body-md text-onSurface">
              {note.format} · {formatDate(note.createdAt, i18n.language)}
            </span>
            <NoteStatusBadge status={note.status === "Signed" || note.status === "Exported" ? "signed" : "pending"} />
          </Link>
        </li>
      ))}
    </ul>
  );
}
