import { createFileRoute } from "@tanstack/react-router";
import SecurityPage from "@anamnys/shared/components/SecurityPage";

export const Route = createFileRoute("/_app/account/security")({
  component: () => <SecurityPage realm="provider" />,
});
