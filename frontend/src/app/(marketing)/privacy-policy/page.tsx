import { readFile } from "fs/promises";
import path from "path";
import LegalDocument from "@/components/LegalDocument";

export default async function PrivacyPolicyPage() {
  const markdown = await readFile(path.join(process.cwd(), "src/content/legal/privacy-policy.md"), "utf-8");
  return <LegalDocument markdown={markdown} />;
}
