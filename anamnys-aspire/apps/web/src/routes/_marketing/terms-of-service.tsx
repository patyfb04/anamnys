import { createFileRoute } from "@tanstack/react-router";
import LegalDocument from "@/components/LegalDocument";
import markdown from "@/content/legal/terms-of-service.md?raw";

export const Route = createFileRoute("/_marketing/terms-of-service")({
  component: TermsOfServicePage,
});

function TermsOfServicePage() {
  return <LegalDocument markdown={markdown} />;
}
