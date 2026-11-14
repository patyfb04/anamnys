import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useNavigate } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { ApiError } from "@anamnys/shared/api/client";
import { intakeApi } from "@anamnys/shared/api/intake";
import type { IntakeAnswerType, IntakeTargetField } from "@anamnys/shared/lib/types";
import { toast } from "@anamnys/shared/ui/Toaster";

// Structured intake (F-31) helpers shared by the patient card, the intake page, the Note Editor shortcut and settings.

export const intakesKey = (patientId: string) => ["intakes", patientId] as const;
export const intakeKey = (intakeId: string) => ["intake", intakeId] as const;

// The "Levar para o prontuário" panel on the intake page (linked from the patient card).
export const TRANSFERS_ANCHOR = "levar-ao-prontuario";

// i18n keys cannot hold dots, so "patient.phone" is looked up as "patient_phone".
export const targetLabelKey = (target: IntakeTargetField) => `intake.targets.${target.replace(".", "_")}`;

// The answer type a destination needs (mirrors IntakeRules.AnswerTypeFor on the server).
export const answerTypeFor = (target: IntakeTargetField): IntakeAnswerType =>
  target === "patient.dateOfBirth" ? "date" : "text";

export const errorCode = (e: unknown) => (e instanceof ApiError ? e.code : undefined);

export const questionIdsOf = (e: unknown): string[] => {
  if (!(e instanceof ApiError)) return [];
  const body = e.body as { questionIds?: string[] } | undefined;
  return body?.questionIds ?? [];
};

export function usePatientIntakes(patientId: string | undefined) {
  return useQuery({
    queryKey: intakesKey(patientId ?? ""),
    queryFn: () => intakeApi.listForPatient(patientId!),
    enabled: !!patientId,
  });
}

// "Iniciar anamnese": creates the draft with the provider's default template and opens it. A draft that already
// exists (409 intake_in_progress) is opened instead.
export function useStartIntake(patientId: string) {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: () => intakeApi.start(patientId),
    onSuccess: (intakeId) => {
      void queryClient.invalidateQueries({ queryKey: intakesKey(patientId) });
      void navigate({ to: "/patients/$patientId/intake/$intakeId", params: { patientId, intakeId } });
    },
    onError: async (e) => {
      const code = errorCode(e);
      if (code === "intake_in_progress") {
        const list = await queryClient.fetchQuery({
          queryKey: intakesKey(patientId),
          queryFn: () => intakeApi.listForPatient(patientId),
        });
        const draft = list.find((i) => i.status === "draft");
        if (draft) {
          void navigate({ to: "/patients/$patientId/intake/$intakeId", params: { patientId, intakeId: draft.id } });
          return;
        }
        toast.error(t("intake.card.inProgress"));
      } else if (code === "patient_archived") toast.error(t("intake.card.patientArchived"));
      else toast.error(t("intake.card.startFailed"));
    },
  });
}

// Discarding a draft intake also deletes its unsigned note.
export function useDiscardIntake(patientId: string, onDone?: () => void) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: (intakeId: string) => intakeApi.discard(intakeId),
    onSuccess: (_, intakeId) => {
      // Leave the intake page first, so it never flashes empty after its query is removed.
      onDone?.();
      queryClient.removeQueries({ queryKey: intakeKey(intakeId) });
      void queryClient.invalidateQueries({ queryKey: intakesKey(patientId) });
      void queryClient.invalidateQueries({ queryKey: ["notes"] });
      toast.success(t("intake.discard.done"));
    },
    onError: () => toast.error(t("intake.discard.failed")),
  });
}
