"use client";

import { useState } from "react";
import { ChevronDown, Bot, Mic, ShieldCheck, Scale, Smartphone, CreditCard, Download, type LucideIcon } from "lucide-react";
import FaqAnswer from "@/components/FaqAnswer";

export type FaqIconKey = "bot" | "mic" | "shield" | "scale" | "smartphone" | "creditCard" | "download";

// Icon components can't cross the server/client boundary as props (FaqCategory is built in
// page.tsx, a server component, and passed down to this client component) — so the server
// side only ever hands over a serializable key, resolved to the actual component here.
const ICONS: Record<FaqIconKey, LucideIcon> = {
  bot: Bot,
  mic: Mic,
  shield: ShieldCheck,
  scale: Scale,
  smartphone: Smartphone,
  creditCard: CreditCard,
  download: Download,
};

export interface FaqCategory {
  title: string;
  iconKey: FaqIconKey;
  items: { question: string; answer: string }[];
}

function FaqItem({ question, answer }: { question: string; answer: string }) {
  const [open, setOpen] = useState(false);
  return (
    <div className="border border-outlineVariant/30 rounded-radii-md bg-surfaceContainerLowest">
      <button
        type="button"
        onClick={() => setOpen((o) => !o)}
        className="w-full flex items-center justify-between gap-3 px-4 py-3 text-left"
        aria-expanded={open}
      >
        <span className="text-label-lg text-onSurface">{question}</span>
        <ChevronDown size={18} className={`text-onSurfaceVariant shrink-0 transition-transform ${open ? "rotate-180" : ""}`} />
      </button>
      {open && (
        <div className="px-4 pb-4">
          <FaqAnswer markdown={answer} />
        </div>
      )}
    </div>
  );
}

// Category cards in a responsive grid, each holding independently-toggleable question rows.
// Matches specs/UI/FAQ/screen.png's card + chevron-accordion pattern; icons are all purple
// (text-primary) per the same convention already applied on the About Us page.
export default function FaqAccordion({ categories }: { categories: FaqCategory[] }) {
  return (
    <div className="grid grid-cols-1 md:grid-cols-2 gap-6">
      {categories.map((category, i) => {
        const isLast = i === categories.length - 1 && categories.length % 2 === 1;
        const Icon = ICONS[category.iconKey];
        return (
          <div
            key={category.title}
            className={`bg-surfaceContainerLow rounded-radii-xl border border-outlineVariant/30 p-6 ${isLast ? "md:col-span-2" : ""}`}
          >
            <div className="flex items-center gap-2.5 mb-4 text-primary">
              <Icon size={20} />
              <h2 className="text-headline-sm text-[18px] text-primary">{category.title}</h2>
            </div>
            <div className="flex flex-col gap-2.5">
              {category.items.map((item) => (
                <FaqItem key={item.question} question={item.question} answer={item.answer} />
              ))}
            </div>
          </div>
        );
      })}
    </div>
  );
}
