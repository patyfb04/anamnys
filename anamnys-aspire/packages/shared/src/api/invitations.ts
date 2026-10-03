import api from "@anamnys/shared/api/client";

// Patient side of portal invitations: accepting links the signed-in account to the
// provider's record. Failures reject with ApiError.code "invalid" | "email_mismatch" |
// "already_linked".
export const invitationsApi = {
  accept: async (token: string): Promise<{ providerName: string }> => {
    const { data } = await api.post<{ providerName: string }>("/phi/patients/me/invitations/accept", { token });
    return data;
  },
};
