import api from "@/api/client";

// Public "Contact / Support" form (unauthenticated — no cookie/session needed, unlike every
// other API here). Backed by POST /api/contact on ClinicalDraft.Api's ContactController.
export const contactApi = {
  submit: async (fullName: string, email: string, phone: string | undefined, message: string): Promise<void> => {
    await api.post("/contact", { fullName, email, phone: phone || null, message });
  },
};
