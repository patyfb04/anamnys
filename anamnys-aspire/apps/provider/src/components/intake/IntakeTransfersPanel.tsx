import { useEffect, useRef } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { ArrowRightLeft, Check, X } from "lucide-react";
import { intakeApi } from "@anamnys/shared/api/intake";
import type { IntakeTransfer } from "@anamnys/shared/lib/types";
import Badge from "@anamnys/shared/ui/Badge";
import { toast } from "@anamnys/shared/ui/Toaster";
import DetailCard from "@/components/patients/detail/DetailCard";
import { formatDay } from "@/components/patients/format";
import { TRANSFERS_ANCHOR, errorCode, intakeKey, intakesKey, targetLabelKey } from "./intakeModel";

// "Levar para o prontuário": each answer with a record destination, confirmed or ignored one by one. Nothing is
// written to the record before the provider presses Confirmar on that item.
export default function IntakeTransfersPanel(props: { intakeId: string; patientId: string; readOnly: boolean; focus?: boolean }) {
  const { intakeId, patientId, readOnly, focus } = props;
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const ref = useRef<HTMLDivElement>(null);
  const query = useQuery({ queryKey: ["intake-transfers", intakeId], queryFn: () => intakeApi.transfers(intakeId) });

  // Arriving from the patient card's "N itens para levar ao prontuário" (hash) or right after signing the note (focus).
  useEffect(() => {
    if (query.isSuccess && (focus || window.location.hash === `#${TRANSFERS_ANCHOR}`)) {
      ref.current?.scrollIntoView({ behavior: "smooth", block: "start" });
    }
  }, [query.isSuccess, focus]);

  const decide = useMutation({
    mutationFn: ({ transfer, confirm }: { transfer: IntakeTransfer; confirm: boolean }) =>
      confirm ? intakeApi.confirmTransfer(intakeId, transfer.id) : intakeApi.ignoreTransfer(intakeId, transfer.id),
    onSuccess: (updated, { confirm }) => {
      queryClient.setQueryData<IntakeTransfer[]>(["intake-transfers", intakeId], (old) =>
        old?.map((x) => (x.id === updated.id ? updated : x)),
      );
      void queryClient.invalidateQueries({ queryKey: intakeKey(intakeId) });
      void queryClient.invalidateQueries({ queryKey: intakesKey(patientId) });
      if (confirm) {
        void queryClient.invalidateQueries({ queryKey: ["patient", patientId] });
        void queryClient.invalidateQueries({ queryKey: ["patients"] });
        void queryClient.invalidateQueries({ queryKey: ["timeline", patientId] });
        void queryClient.invalidateQueries({ queryKey: ["plan-adherence", patientId] });
      }
    },
    onError: (e) => {
      toast.error(errorCode(e) === "patient_archived" ? t("intake.transfers.patientArchived") : t("intake.transfers.failed"));
      void query.refetch();
    },
  });

  const transfers = query.data ?? [];

  return (
    <div ref={ref} id={TRANSFERS_ANCHOR} className="scroll-mt-24">
      <DetailCard title={t("intake.transfers.title")} icon={<ArrowRightLeft size={20} />}>
        <p className="mb-4 text-body-md text-onSurfaceVariant">{t("intake.transfers.description")}</p>
        {query.isError ? (
          <p className="text-body-md text-error">{t("intake.transfers.loadFailed")}</p>
        ) : query.isPending ? null : transfers.length === 0 ? (
          <p className="text-body-md text-onSurfaceVariant">{t("intake.transfers.empty")}</p>
        ) : (
          <ul className="flex flex-col gap-3">
            {transfers.map((x) => (
              <TransferRow
                key={x.id}
                transfer={x}
                readOnly={readOnly}
                busy={decide.isPending && decide.variables?.transfer.id === x.id}
                onDecide={(confirm) => decide.mutate({ transfer: x, confirm })}
              />
            ))}
          </ul>
        )}
      </DetailCard>
    </div>
  );
}

function TransferRow(props: { transfer: IntakeTransfer; readOnly: boolean; busy: boolean; onDecide: (confirm: boolean) => void }) {
  const { transfer: x, readOnly, busy, onDecide } = props;
  const { t, i18n } = useTranslation();
  const registration = x.targetField.startsWith("patient.");
  const show = (v: string | null) => {
    if (!v) return t("intake.transfers.empty_value");
    return x.targetField === "patient.dateOfBirth" ? formatDay(v, i18n.language) : v;
  };

  return (
    <li className="flex flex-col gap-3 rounded-radii-lg border border-outlineVariant p-3">
      <div className="flex flex-wrap items-start justify-between gap-2">
        <div className="min-w-0">
          <p className="text-label-md text-onSurfaceVariant">{t(targetLabelKey(x.targetField))}</p>
          <p className="text-body-sm text-onSurfaceVariant">{x.questionLabel}</p>
        </div>
        {x.status !== "pending" && (
          <Badge
            label={x.status === "confirmed" ? t("intake.transfers.confirmed") : t("intake.transfers.ignored")}
            tone={x.status === "confirmed" ? "mint" : "neutral"}
          />
        )}
      </div>
      {registration ? (
        <dl className="grid gap-2 sm:grid-cols-2">
          <div>
            <dt className="text-label-md text-onSurfaceVariant">{t("intake.transfers.current")}</dt>
            <dd className="break-words text-body-md text-onSurface">{show(x.currentValue)}</dd>
          </div>
          <div>
            <dt className="text-label-md text-onSurfaceVariant">{t("intake.transfers.new")}</dt>
            <dd className="break-words text-body-md font-medium text-onSurface">{show(x.value)}</dd>
          </div>
        </dl>
      ) : (
        <p className="whitespace-pre-wrap break-words text-body-md text-onSurface">{x.value}</p>
      )}
      {x.status === "pending" && !readOnly && (
        <div className="flex flex-wrap justify-end gap-2">
          <button
            type="button"
            disabled={busy}
            onClick={() => onDecide(false)}
            className="inline-flex items-center gap-1 rounded-radii-md border border-outlineVariant px-3 py-1.5 text-label-md text-onSurface hover:bg-pastelLavender disabled:opacity-60"
          >
            <X size={14} aria-hidden />
            {t("intake.transfers.ignore")}
          </button>
          <button
            type="button"
            disabled={busy}
            onClick={() => onDecide(true)}
            className="inline-flex items-center gap-1 rounded-radii-md bg-primary px-3 py-1.5 text-label-md text-onPrimary hover:opacity-90 disabled:opacity-60"
          >
            <Check size={14} aria-hidden />
            {t("intake.transfers.confirm")}
          </button>
        </div>
      )}
    </li>
  );
}
