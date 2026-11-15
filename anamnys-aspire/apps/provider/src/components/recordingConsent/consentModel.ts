import { useQuery } from "@tanstack/react-query";
import { ApiError } from "@anamnys/shared/api/client";
import { recordingConsentApi } from "@anamnys/shared/api/recordingConsent";

// Recording consent (F-23/F-24) helpers shared by the patient card, the header badge and Sessão ao vivo.
export const consentKey = (patientId: string) => ["recording-consent", patientId] as const;

export function useRecordingConsent(patientId: string | undefined) {
  return useQuery({
    queryKey: consentKey(patientId ?? ""),
    queryFn: () => recordingConsentApi.get(patientId!),
    enabled: !!patientId,
    // The state can change under the page (the signer signs on another device): look again on focus.
    staleTime: 0,
  });
}

export const errorCode = (e: unknown) => (e instanceof ApiError ? e.code : undefined);
