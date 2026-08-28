import { type FaqCategory, type FaqIconKey } from "@/components/FaqAccordion";

// One icon per real category in src/content/site/faq.md, in file order.
const CATEGORY_ICON_KEYS: Record<string, FaqIconKey> = {
  "Sobre a inteligência artificial": "bot",
  "Sobre gravação de sessão": "mic",
  "Sobre segurança e privacidade": "shield",
  "Sobre responsabilidade profissional": "scale",
  "Sobre uso prático": "smartphone",
  "Sobre assinatura e pagamento": "creditCard",
  "Se a ferramenta deixar de existir": "download",
};

// Splits the real FAQ markdown (## Category > ### Question > answer) into structured data
// instead of hand-transcribing ~35 rich-formatted Q&A pairs into i18n keys. The closing
// "Ainda tem dúvida?" section isn't a Q&A category — it's parsed out and dropped here; the
// page renders its own closing CTA from i18n instead (matches Contact/Pricing/About Us, which
// all keep their own page-chrome text in pt.json rather than sourced from markdown).
export function parseFaq(markdown: string): FaqCategory[] {
  const afterIntro = markdown.slice(markdown.indexOf("\n## "));
  const categoryBlocks = afterIntro.split(/\n## /).filter(Boolean);

  const categories: FaqCategory[] = [];

  for (const block of categoryBlocks) {
    const newlineIndex = block.indexOf("\n");
    const title = block.slice(0, newlineIndex).trim();
    if (title === "Ainda tem dúvida?") continue; // closing prompt, not a Q&A category

    const rest = block.slice(newlineIndex + 1);
    const questionBlocks = rest.split(/\n### /).slice(1);
    const items = questionBlocks.map((qBlock) => {
      const qNewline = qBlock.indexOf("\n");
      const question = qBlock.slice(0, qNewline).trim();
      const answer = qBlock
        .slice(qNewline + 1)
        .replace(/\n---\s*$/, "")
        .trim();
      return { question, answer };
    });

    categories.push({ title, iconKey: CATEGORY_ICON_KEYS[title] ?? "bot", items });
  }

  return categories;
}
