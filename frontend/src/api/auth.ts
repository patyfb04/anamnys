import api from "@/api/client";
import { LoginRequest, RegisterRequest, LoginResponse, AuthUser } from "@/lib/types";

export const authApi = {
  login: async (req: LoginRequest): Promise<LoginResponse> => {
    const { data } = await api.post<LoginResponse>("/auth/login", req);
    return data;
  },
  register: async (req: RegisterRequest): Promise<LoginResponse> => {
    const { data } = await api.post<LoginResponse>("/auth/register", req);
    return data;
  },
  completeTwoFactorLogin: async (challengeToken: string, code: string): Promise<LoginResponse> => {
    const { data } = await api.post<LoginResponse>("/auth/login/2fa", { challengeToken, code });
    return data;
  },
  logout: async (): Promise<void> => {
    await api.post("/auth/logout").catch(() => undefined);
  },
  me: async (): Promise<AuthUser> => {
    const { data } = await api.get<AuthUser>("/auth/me");
    return data;
  },
};
