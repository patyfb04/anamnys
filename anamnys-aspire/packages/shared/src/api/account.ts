import api from "@anamnys/shared/api/client";

// Password changes and 2FA/TOTP enrolment now live entirely in Keycloak's own
// account console — the application no longer stores credentials or a second
// factor, so there is nothing left here for those flows to call.
export const accountApi = {
  changePassword: async (currentPassword: string, newPassword: string): Promise<void> => {
    await api.post("/account/change-password", { currentPassword, newPassword });
  },
};
