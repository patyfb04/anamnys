import api from "@anamnys/shared/api/client";

// Recording consent (design/specs/2026-11-15-recording-consent-design.md §5). Only ids travel in URLs; the
// provider's note, the assent note and the revocation reason stay in bodies.
const BASE = "/phi/providers/me";

export type ConsentState = "none" | "sent" | "signed" | "revoked" | "expired";
export type ConsentSignerRole = "patient" | "guardian";
export type ConsentExpiredReason = "link" | "discharge" | "adulthood" | "new_version";
// Why capture is (not) allowed: "ok" or the reason a session recording is refused.
export type ConsentCaptureReason = "ok" | "none" | "sent" | "revoked" | "expired" | "assent_missing" | "archived";
// The first sending precondition that fails, null when a new term can be sent.
export type ConsentSendBlockedBy =
  | "profile_incomplete"
  | "patient_email_missing"
  | "patient_birthdate_missing"
  | "patient_archived";

export interface ConsentEvent {
  fromState: string;
  toState: string;
  at: string; // ISO instant
  actorKind: "provider" | "signer" | "system";
  reason: string | null;
}

export interface RecordingConsent {
  id: string | null;
  state: ConsentState;
  version: string | null;
  currentVersion: string;
  sentAt: string | null;
  signedAt: string | null;
  signerRole: ConsentSignerRole | null;
  signerName: string | null;
  guardianRelationship: string | null;
  assentRecordedAt: string | null; // yyyy-MM-dd
  assentNote: string | null;
  revokedAt: string | null;
  revokedBy: "patient" | "provider" | null;
  revokeReason: string | null;
  expiredReason: ConsentExpiredReason | null;
  linkExpiresAt: string | null;
  providerNote: string | null;
  hasDocument: boolean;
  isMinor: boolean;
  canRecord: boolean;
  reason: ConsentCaptureReason;
  sendBlockedBy: ConsentSendBlockedBy | null;
  events: ConsentEvent[];
}

export interface ConsentPreview {
  version: string;
  text: string; // without the provider's observations
  sendBlockedBy: ConsentSendBlockedBy | null;
}

export const recordingConsentApi = {
  get: async (patientId: string): Promise<RecordingConsent> => {
    const { data } = await api.get<RecordingConsent>(`${BASE}/patients/${patientId}/recording-consent`);
    return data;
  },
  preview: async (patientId: string): Promise<ConsentPreview> => {
    const { data } = await api.get<ConsentPreview>(`${BASE}/patients/${patientId}/recording-consent/preview`);
    return data;
  },
  send: async (patientId: string, providerNote: string | null): Promise<RecordingConsent> => {
    const { data } = await api.post<RecordingConsent>(`${BASE}/patients/${patientId}/recording-consent`, {
      providerNote: providerNote || null,
    });
    return data;
  },
  resend: async (id: string): Promise<RecordingConsent> => {
    const { data } = await api.post<RecordingConsent>(`${BASE}/recording-consents/${id}/resend`);
    return data;
  },
  renew: async (id: string): Promise<RecordingConsent> => {
    const { data } = await api.post<RecordingConsent>(`${BASE}/recording-consents/${id}/renew`);
    return data;
  },
  assent: async (id: string, date: string, note: string | null): Promise<RecordingConsent> => {
    const { data } = await api.post<RecordingConsent>(`${BASE}/recording-consents/${id}/assent`, {
      date,
      note: note || null,
    });
    return data;
  },
  revoke: async (id: string, reason: string | null): Promise<RecordingConsent> => {
    const { data } = await api.post<RecordingConsent>(`${BASE}/recording-consents/${id}/revoke`, {
      reason: reason || null,
    });
    return data;
  },
  document: async (id: string): Promise<Blob> => {
    const { data } = await api.get<Blob>(`${BASE}/recording-consents/${id}/document`, { responseType: "blob" });
    return data;
  },
};
