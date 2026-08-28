import api from "@anamnys/shared/api/client";
import type { TwoFactorStatus, TwoFactorSetup, TwoFactorVerifyResult } from "@anamnys/shared/lib/types";

export const accountApi = {
  changePassword: async (currentPassword: string, newPassword: string): Promise<void> => {
    await api.post("/account/change-password", { currentPassword, newPassword });
  },
  twoFactorStatus: async (): Promise<TwoFactorStatus> => {
    const { data } = await api.get<TwoFactorStatus>("/account/2fa/status");
    return data;
  },
  twoFactorSetup: async (): Promise<TwoFactorSetup> => {
    const { data } = await api.post<TwoFactorSetup>("/account/2fa/setup");
    return data;
  },
  twoFactorVerify: async (code: string): Promise<TwoFactorVerifyResult> => {
    const { data } = await api.post<TwoFactorVerifyResult>("/account/2fa/verify", { code });
    return data;
  },
  twoFactorDisable: async (password: string, code: string): Promise<void> => {
    await api.post("/account/2fa/disable", { password, code });
  },
};
