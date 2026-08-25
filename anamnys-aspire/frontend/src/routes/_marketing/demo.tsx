import { createFileRoute } from "@tanstack/react-router";
import MarketingPlaceholder from "@/components/MarketingPlaceholder";

export const Route = createFileRoute("/_marketing/demo")({
  component: DemoPage,
});

function DemoPage() {
  return <MarketingPlaceholder titleKey="welcome.watchDemo" />;
}
