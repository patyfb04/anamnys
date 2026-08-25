import { createFileRoute } from "@tanstack/react-router";
import LegalDocument from "@/components/LegalDocument";
import markdown from "@/content/legal/privacy-policy.md?raw";

export const Route = createFileRoute("/_marketing/privacy-policy")({
  component: PrivacyPolicyPage,
});

function PrivacyPolicyPage() {
  return <LegalDocument markdown={markdown} />;
}
