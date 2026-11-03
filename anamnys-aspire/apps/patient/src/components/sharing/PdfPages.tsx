import { useEffect, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { GlobalWorkerOptions, getDocument, type PDFDocumentLoadingTask } from "pdfjs-dist";
import workerUrl from "pdfjs-dist/build/pdf.worker.min.mjs?url";

GlobalWorkerOptions.workerSrc = workerUrl;

// Draws every page of the shared PDF on canvases, so it shows on any device, including Android Chrome, which has no
// inline PDF viewer and would otherwise only download the file. No text layer: the recipient reads, doesn't copy.
export default function PdfPages({ blob, label }: { blob: Blob; label: string }) {
  const { t } = useTranslation();
  const container = useRef<HTMLDivElement>(null);
  const [failed, setFailed] = useState(false);

  useEffect(() => {
    const host = container.current;
    if (!host) return;
    let cancelled = false;
    let task: PDFDocumentLoadingTask | null = null;

    (async () => {
      try {
        const data = new Uint8Array(await blob.arrayBuffer());
        task = getDocument({ data, enableXfa: false });
        const pdf = await task.promise;
        if (cancelled) return;
        host.replaceChildren();
        const width = host.clientWidth || 800;
        const ratio = window.devicePixelRatio || 1;
        for (let n = 1; n <= pdf.numPages; n++) {
          const page = await pdf.getPage(n);
          if (cancelled) return;
          const base = page.getViewport({ scale: 1 });
          const viewport = page.getViewport({ scale: (width / base.width) * ratio });
          const canvas = document.createElement("canvas");
          canvas.width = Math.floor(viewport.width);
          canvas.height = Math.floor(viewport.height);
          canvas.style.width = "100%";
          canvas.className = "block rounded-radii-md border border-outlineVariant bg-white shadow-sm";
          canvas.setAttribute("role", "img");
          canvas.setAttribute("aria-label", t("sharing.public.pageLabel", { page: n, total: pdf.numPages }));
          host.appendChild(canvas);
          await page.render({ canvas, viewport }).promise;
        }
      } catch {
        if (!cancelled) setFailed(true);
      }
    })();

    return () => {
      cancelled = true;
      void task?.destroy();
    };
  }, [blob, t]);

  return (
    <>
      <div ref={container} aria-label={label} className="flex w-full flex-col gap-4" />
      {failed && <p className="text-body-md text-onSurfaceVariant">{t("sharing.public.noViewer")}</p>}
    </>
  );
}
