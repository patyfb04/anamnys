import api from "@anamnys/shared/api/client";

// Public, no session: the token in the e-mailed link is the only credential (design/specs/2026-11-15-recording-
// consent-design.md §4). The date of birth is typed by the signer and sent once; the server never returns it.
export interface PublicConsentView {
  text: string;
  guardianExpected: boolean;
  providerName: string;
}

export interface PublicRevokeView {
  text: string;
  signedAt: string;
  signerName: string;
  termSha256: string;
  providerName: string;
}

export interface PublicSignRequest {
  birthDate: string; // yyyy-MM-dd
  signerName: string;
  relationship?: string;
  accepted: boolean;
}

const BASE = "/public/recording-consents";
const enc = encodeURIComponent;

export const publicConsentApi = {
  view: async (token: string): Promise<PublicConsentView> => {
    const { data } = await api.get<PublicConsentView>(`${BASE}/${enc(token)}`);
    return data;
  },
  sign: async (token: string, request: PublicSignRequest): Promise<void> => {
    await api.post(`${BASE}/${enc(token)}`, request);
  },
  revokeView: async (token: string): Promise<PublicRevokeView> => {
    const { data } = await api.get<PublicRevokeView>(`${BASE}/revoke/${enc(token)}`);
    return data;
  },
  revoke: async (token: string, birthDate: string): Promise<void> => {
    await api.post(`${BASE}/revoke/${enc(token)}`, { birthDate });
  },
};
