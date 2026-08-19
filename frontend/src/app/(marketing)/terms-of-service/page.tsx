import { readFile } from "fs/promises";
import path from "path";
import LegalDocument from "@/components/LegalDocument";

export default async function TermsOfServicePage() {
  const markdown = await readFile(path.join(process.cwd(), "src/content/legal/terms-of-service.md"), "utf-8");
  return <LegalDocument markdown={markdown} />;
}
