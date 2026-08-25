import { createFileRoute } from "@tanstack/react-router";
import MarketingPlaceholder from "@/components/MarketingPlaceholder";

export const Route = createFileRoute("/_marketing/solutions")({
  component: SolutionsPage,
});

function SolutionsPage() {
  return <MarketingPlaceholder titleKey="welcome.nav.solutions" />;
}
