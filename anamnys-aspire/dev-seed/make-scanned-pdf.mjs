// Dev-only: wraps dev-seed/sample-documents/_scan-page.jpg into an image-only PDF (no text layer),
// so the reader must render the page and OCR it. Run after make-scanned-samples.ps1.
//   node dev-seed/make-scanned-pdf.mjs
import { readFileSync, writeFileSync, unlinkSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";

const dir = join(dirname(fileURLToPath(import.meta.url)), "sample-documents");
const jpg = readFileSync(join(dir, "_scan-page.jpg"));
// JPEG size from the SOF0/SOF2 marker.
let i = 2, w = 0, h = 0;
while (i < jpg.length) {
  const marker = jpg[i + 1];
  const len = jpg.readUInt16BE(i + 2);
  if (marker === 0xc0 || marker === 0xc2) { h = jpg.readUInt16BE(i + 5); w = jpg.readUInt16BE(i + 7); break; }
  i += 2 + len;
}
const content = Buffer.from(`q 595 0 0 842 0 0 cm /Im0 Do Q`, "latin1");
const objects = [
  "<< /Type /Catalog /Pages 2 0 R >>",
  "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
  "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>",
  { head: `<< /Type /XObject /Subtype /Image /Width ${w} /Height ${h} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length ${jpg.length} >>`, data: jpg },
  { head: `<< /Length ${content.length} >>`, data: content },
];
const parts = [Buffer.from("%PDF-1.4\n%\xe2\xe3\xcf\xd3\n", "latin1")];
const offsets = [];
let length = parts[0].length;
objects.forEach((o, n) => {
  offsets.push(length);
  const chunk = typeof o === "string"
    ? Buffer.from(`${n + 1} 0 obj\n${o}\nendobj\n`, "latin1")
    : Buffer.concat([Buffer.from(`${n + 1} 0 obj\n${o.head}\nstream\n`, "latin1"), o.data, Buffer.from("\nendstream\nendobj\n", "latin1")]);
  parts.push(chunk);
  length += chunk.length;
});
const xref = `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n` + offsets.map((o) => `${String(o).padStart(10, "0")} 00000 n \n`).join("");
parts.push(Buffer.from(`${xref}trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${length}\n%%EOF\n`, "latin1"));
writeFileSync(join(dir, "fernanda-barbosa-laudo-psicopedagogico-escaneado.pdf"), Buffer.concat(parts));
unlinkSync(join(dir, "_scan-page.jpg"));
console.log("fernanda-barbosa-laudo-psicopedagogico-escaneado.pdf");
