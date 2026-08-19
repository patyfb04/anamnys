import MarketingHeader from "@/components/MarketingHeader";
import MarketingFooter from "@/components/MarketingFooter";

// Shared chrome for every public marketing page besides the landing page itself (which lives
// at the top-level "/" route, outside this group, so it can build its own hero instead of a
// plain placeholder body) — same sticky header and light-gray footer as the landing page.
export default function MarketingLayout({ children }: { children: React.ReactNode }) {
  return (
    <div className="min-h-screen bg-surface flex flex-col">
      <MarketingHeader />
      <main className="flex-1">{children}</main>
      <MarketingFooter />
    </div>
  );
}
