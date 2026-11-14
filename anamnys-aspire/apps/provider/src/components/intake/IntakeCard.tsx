import { useState } from "react";
import { Link } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { ArrowRightLeft, ClipboardList, Loader2, Plus, Trash2 } from "lucide-react";
import type { IntakeListItem } from "@anamnys/shared/lib/types";
import Badge from "@anamnys/shared/ui/Badge";
import DetailCard from "@/components/patients/detail/DetailCard";
import ConfirmDialog from "@/components/patients/ConfirmDialog";
import { formatDate } from "@/components/patients/format";
import { TRANSFERS_ANCHOR, useDiscardIntake, usePatientIntakes, useStartIntake } from "./intakeModel";

// Patient page: the latest intake, earlier ones, "Iniciar anamnese" (highlighted while there is none) and the
// count of answers waiting to be taken to the record (design/specs/2026-11-12-structured-intake-design.md §5).
export default function IntakeCard({ patientId, readOnly }: { patientId: string; readOnly: boolean }) {
  const { t } = useTranslation();
  const query = usePatientIntakes(patientId);
  const start = useStartIntake(patientId);
  const [discarding, setDiscarding] = useState<string | null>(null);
  const discard = useDiscardIntake(patientId, () => setDiscarding(null));

  const intakes = query.data ?? [];
  const [latest, ...history] = intakes;
  const hasDraft = intakes.some((i) => i.status === "draft");
  const canStart = !readOnly && !hasDraft && query.isSuccess;

  const startButton = canStart && (
    <button
      type="button"
      disabled={start.isPending}
      onClick={() => start.mutate()}
      className={
        intakes.length === 0
          ? "inline-flex items-center gap-2 rounded-radii-md bg-primary px-4 py-2 text-label-lg text-onPrimary hover:opacity-90 disabled:opacity-60"
          : "inline-flex items-center gap-1 rounded-radii-md border border-outlineVariant px-3 py-1.5 text-label-md text-primary hover:bg-pastelLavender disabled:opacity-60"
      }
    >
      {start.isPending ? <Loader2 size={16} className="animate-spin" aria-hidden /> : <Plus size={16} aria-hidden />}
      {t("intake.card.start")}
    </button>
  );

  return (
    <DetailCard title={t("intake.card.title")} icon={<ClipboardList size={20} />} action={intakes.length > 0 ? startButton : undefined}>
      {query.isError ? (
        <p className="text-body-md text-error">{t("intake.card.loadFailed")}</p>
      ) : query.isPending ? null : !latest ? (
        <div className="flex flex-col items-start gap-3 rounded-radii-lg bg-primaryFixed/40 p-4">
          <p className="text-body-md text-onSurface">{t("intake.card.none")}</p>
          {startButton}
        </div>
      ) : (
        <div className="flex flex-col gap-4">
          <IntakeRow patientId={patientId} intake={latest} readOnly={readOnly} onDiscard={setDiscarding} prominent />
          {history.length > 0 && (
            <div className="flex flex-col gap-2">
              <h3 className="text-label-lg text-onSurfaceVariant">{t("intake.card.history")}</h3>
              <ul className="flex flex-col gap-2">
                {history.map((i) => (
                  <li key={i.id}>
                    <IntakeRow patientId={patientId} intake={i} readOnly={readOnly} onDiscard={setDiscarding} />
                  </li>
                ))}
              </ul>
            </div>
          )}
        </div>
      )}
      <ConfirmDialog
        open={discarding !== null}
        onOpenChange={(open) => !open && setDiscarding(null)}
        title={t("intake.discard.title")}
        description={t("intake.discard.body")}
        confirmLabel={t("intake.discard.confirm")}
        destructive
        pending={discard.isPending}
        onConfirm={() => discarding && discard.mutate(discarding)}
      />
    </DetailCard>
  );
}

function IntakeRow(props: {
  patientId: string;
  intake: IntakeListItem;
  readOnly: boolean;
  onDiscard: (id: string) => void;
  prominent?: boolean;
}) {
  const { patientId, intake, readOnly, onDiscard, prominent } = props;
  const { t, i18n } = useTranslation();
  const draft = intake.status === "draft";
  const date = draft
    ? t("intake.card.startedOn", { date: formatDate(intake.createdAt, i18n.language) })
    : t("intake.card.signedOn", { date: formatDate(intake.signedAt, i18n.language) });
  const linkParams = { patientId, intakeId: intake.id };

  return (
    <div
      className={`flex flex-col gap-2 rounded-radii-lg border p-3 ${
        prominent && draft ? "border-transparent bg-surfaceContainerLow" : "border-outlineVariant"
      }`}
    >
      <div className="flex flex-wrap items-center justify-between gap-2">
        <div className="flex flex-wrap items-center gap-2">
          <Badge label={t(`intake.status.${intake.status}`)} tone={draft ? "blue" : "mint"} />
          <span className="text-body-sm text-onSurfaceVariant">{date}</span>
        </div>
        <div className="flex items-center gap-2">
          {draft && !readOnly && (
            <button
              type="button"
              onClick={() => onDiscard(intake.id)}
              className="inline-flex items-center gap-1 rounded-radii-md px-2 py-1.5 text-label-md text-error hover:bg-errorContainer"
            >
              <Trash2 size={14} aria-hidden />
              {t("intake.card.discardDraft")}
            </button>
          )}
          <Link
            to="/patients/$patientId/intake/$intakeId"
            params={linkParams}
            className={`rounded-radii-md px-3 py-1.5 text-label-md ${
              draft ? "border border-primary text-primary hover:bg-primaryFixed" : "text-onSurfaceVariant hover:text-primary"
            }`}
          >
            {draft ? t("intake.card.continue") : t("intake.card.open")}
          </Link>
        </div>
      </div>
      {intake.pendingTransfers > 0 && (
        <Link
          to="/patients/$patientId/intake/$intakeId"
          params={linkParams}
          hash={TRANSFERS_ANCHOR}
          className="inline-flex items-center gap-1 self-start text-label-md text-primary hover:underline"
        >
          <ArrowRightLeft size={14} aria-hidden />
          {t("intake.card.pending", { count: intake.pendingTransfers })}
        </Link>
      )}
    </div>
  );
}
