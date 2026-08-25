import { createFileRoute } from "@tanstack/react-router";
import FaqPageView from "@/components/FaqPageView";
import { parseFaq } from "@/lib/faq";
import markdown from "@/content/site/faq.md?raw";

export const Route = createFileRoute("/_marketing/faq")({
  component: FaqPage,
});

// Parsed once at module load — faq.md is bundled statically (no server-side fs read available
// in a Vite SPA), so this replaces the Next version's per-request readFile + parse.
const categories = parseFaq(markdown);

function FaqPage() {
  return <FaqPageView categories={categories} />;
}
