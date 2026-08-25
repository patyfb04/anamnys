import { createFileRoute, Outlet } from "@tanstack/react-router";
import MarketingHeader from "@/components/MarketingHeader";
import MarketingFooter from "@/components/MarketingFooter";

export const Route = createFileRoute("/_marketing")({
  component: MarketingLayout,
});

// Shared chrome for every public marketing page besides the landing page itself (which lives
// at the top-level "/" route, outside this layout, so it can build its own hero instead of a
// plain placeholder body) — same sticky header and light-gray footer as the landing page.
function MarketingLayout() {
  return (
    <div className="min-h-screen bg-surface flex flex-col">
      <MarketingHeader />
      <main className="flex-1">
        <Outlet />
      </main>
      <MarketingFooter />
    </div>
  );
}
