import { createContext, type ReactNode } from "react";

// What a page puts at the bottom of every open card inside it (the Análises page: back to the top).
export const CardFooterContext = createContext<ReactNode>(null);

export const OPEN_CARD_EVENT = "anamnys:open-card";

// Opens a collapsible card, wherever it is on the page, and scrolls to it.
export function goToCard(id: string) {
  window.dispatchEvent(new CustomEvent(OPEN_CARD_EVENT, { detail: id }));
  requestAnimationFrame(() => document.getElementById(`card-${id}`)?.scrollIntoView({ behavior: "smooth", block: "start" }));
}
