import api, { ApiError } from "@anamnys/shared/api/client";
import type {
  CreatedShareLink,
  CreateShareLinkRequest,
  DisclosureAuthorizationInput,
  DocumentShares,
  ShareOverviewPage,
  SharedDocument,
} from "@anamnys/shared/lib/types";

const BASE = "/phi/providers/me";
// The optional term can be 20 MB over a slow link, and it is scanned before the answer.
const UPLOAD_TIMEOUT_MS = 180_000;

// Do not wrap these calls in useMutation or put their inputs in a query key: both keep the
// values in the query cache, and the link password and token must not outlive their use.
export const sharesApi = {
  list: async (documentId: string): Promise<DocumentShares> => {
    const { data } = await api.get<DocumentShares>(`${BASE}/documents/${documentId}/shares`);
    return data;
  },
  /** Every link of the provider, newest first, 20 per page. Only status and page travel in the URL. */
  overview: async (status: "active" | "all", page: number): Promise<ShareOverviewPage> => {
    const { data } = await api.get<ShareOverviewPage>(`${BASE}/shares`, { params: { status, page } });
    return data;
  },
  // `existing` means this recipient and purpose already had an authorization: its id comes back
  // in the 409 body, which the shared client's ApiError would drop.
  authorize: async (documentId: string, input: DisclosureAuthorizationInput): Promise<{ id: string; existing: boolean }> => {
    const form = new FormData();
    form.append("recipientName", input.recipientName);
    form.append("recipientRole", input.recipientRole);
    form.append("purpose", input.purpose);
    form.append("authorizedAt", input.authorizedAt);
    form.append("obtainedVia", input.obtainedVia);
    form.append("confirmed", "true");
    if (input.term) form.append("term", input.term);
    // `false` drops the instance's JSON default so the browser sets multipart/form-data with its boundary.
    const { data, status } = await api.post<{ id: string; code?: string; message?: string }>(
      `${BASE}/documents/${documentId}/disclosure-authorizations`,
      form,
      {
        timeout: UPLOAD_TIMEOUT_MS,
        headers: { "Content-Type": false },
        validateStatus: (s) => (s >= 200 && s < 300) || s === 409,
      },
    );
    if (status === 409 && data.code !== "authorization_exists") {
      throw new ApiError(data.message ?? "", status, undefined, data.code);
    }
    return { id: data.id, existing: status === 409 };
  },
  createLink: async (documentId: string, request: CreateShareLinkRequest): Promise<CreatedShareLink> => {
    const { data } = await api.post<CreatedShareLink>(`${BASE}/documents/${documentId}/share-links`, request);
    return data;
  },
  revoke: async (linkId: string): Promise<void> => {
    await api.post(`${BASE}/share-links/${linkId}/revoke`);
  },
  /** Each call is audited, and the file comes back in this response: there is no link to it. */
  openTerm: async (authorizationId: string): Promise<Blob> => {
    const { data } = await api.post<Blob>(`${BASE}/disclosure-authorizations/${authorizationId}/term/open`, undefined, {
      responseType: "blob",
    });
    return data;
  },
};

// Public, no session. The token travels only in POST bodies, never in an API URL.
export const publicSharesApi = {
  /** Resolves when the link can be tried; a 404 rejects with an ApiError. Uses no view. */
  check: async (token: string): Promise<void> => {
    await api.post("/public/shares/check", { token });
  },
  // Every successful call uses one view and renders a new PDF, so call it once per password
  // and build the viewer and the download from the same blob. Content-Disposition: attachment
  // is the server's signal that downloading is allowed.
  open: async (token: string, password: string): Promise<SharedDocument> => {
    const response = await api.post<Blob>("/public/shares/open", { token, password }, { responseType: "blob" });
    const disposition = String(response.headers["content-disposition"] ?? "");
    const allowed = /^\s*attachment/i.test(disposition);
    const name = /filename="?([^";]+)"?/i.exec(disposition)?.[1] ?? "documento.pdf";
    return { blob: response.data, downloadName: allowed ? name : null };
  },
};
