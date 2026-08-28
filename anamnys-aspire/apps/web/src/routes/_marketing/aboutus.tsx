import { createFileRoute } from "@tanstack/react-router";
import { Trans, useTranslation } from "react-i18next";
import { BookOpen, Sparkles, Users, ShieldCheck, type LucideIcon } from "lucide-react";

export const Route = createFileRoute("/_marketing/aboutus")({
  component: AboutUsPage,
});

// Bold spans stay the same gray as the surrounding body text (just heavier weight) — only
// the h2 block titles get the darker onSurface color, matching specs/UI/AboutUs/screen.png.
const boldComponent = { b: <strong className="font-bold" /> };

const BOX_CLASS = "bg-surfaceContainerLowest border border-outlineVariant/30 rounded-radii-xl p-6";

function BlockHeading({ icon: Icon, children }: { icon: LucideIcon; children: string }) {
  return (
    <div className="flex items-center gap-2.5 mb-4 text-primary">
      <Icon size={22} />
      <h2 className="text-headline-md text-[22px] text-primary">{children}</h2>
    </div>
  );
}

// Real copy from specs/site/Sobre_Nos.md, rendered as-is (this file's own language rules
// already avoid the terms specs/site/_Notas_de_Implementacao.md flags — "anonimizado", CFP
// endorsement claims, clinical-interpretation verbs, absolute security claims — so nothing to
// filter here). [SEU NOME] and [E-MAIL] stay as literal placeholders per the same "don't invent
// business specifics" rule already applied to the legal pages and Contact's address.
//
// Icons are reused across blocks rather than needing one unique icon each — same pattern the
// mockup itself follows.
function AboutUsPage() {
  const { t } = useTranslation();
  const commitments = t("about.block4.items", { returnObjects: true }) as string[];

  return (
    <div className="max-w-4xl mx-auto px-4 md:px-12 py-12 md:py-16">
      <div className="text-center mb-10">
        <h1 className="text-headline-lg text-[28px] md:text-[32px] text-primary mb-3">{t("about.title")}</h1>
        <p className="text-body-md text-onSurfaceVariant">{t("about.subtitle")}</p>
      </div>

      <div className="grid grid-cols-1 md:grid-cols-2 gap-8 mb-14">
        <div className="relative rounded-radii-xl overflow-hidden shadow-lg aspect-2/3 md:aspect-auto md:h-full order-2 md:order-1">
          <img src="/terapist.png" alt="" className="absolute inset-0 w-full h-full object-cover" />
          <div className="absolute inset-0 bg-linear-to-t from-primary/80 via-primary/25 to-transparent" />
        </div>

        <div className={`order-1 md:order-2 ${BOX_CLASS}`}>
          <BlockHeading icon={BookOpen}>
            {t("about.block1.title")}
          </BlockHeading>
          <p className="text-body-md text-onSurfaceVariant mb-4">
            <Trans i18nKey="about.block1.p1" components={boldComponent} />
          </p>
          <p className="text-body-md text-onSurfaceVariant mb-4">
            <Trans i18nKey="about.block1.p2" components={boldComponent} />
          </p>
          <p className="text-body-md text-onSurfaceVariant">
            <Trans i18nKey="about.block1.p3" components={boldComponent} />
          </p>
        </div>
      </div>

      <div className={`mb-8 ${BOX_CLASS}`}>
        <BlockHeading icon={Sparkles}>
          {t("about.block2.title")}
        </BlockHeading>
        <p className="text-body-md text-onSurfaceVariant mb-4">
          <Trans i18nKey="about.block2.p1" components={boldComponent} />
        </p>
        <p className="text-body-md text-onSurfaceVariant mb-4">
          <Trans i18nKey="about.block2.p2" components={boldComponent} />
        </p>
        <p className="text-body-md text-onSurfaceVariant">
          <Trans i18nKey="about.block2.p3" components={boldComponent} />
        </p>
      </div>

      <div className={`mb-8 ${BOX_CLASS}`}>
        <BlockHeading icon={Users}>
          {t("about.block3.title")}
        </BlockHeading>
        <p className="text-body-md text-onSurfaceVariant mb-4">
          <Trans i18nKey="about.block3.p1" components={boldComponent} />
        </p>
        <p className="text-body-md text-onSurfaceVariant mb-4">{t("about.block3.p2")}</p>
        <p className="text-body-md text-onSurfaceVariant mb-4">{t("about.block3.p3")}</p>
        <p className="text-body-md text-onSurfaceVariant font-medium mb-4">{t("about.block3.riskLead")}</p>
        <p className="text-body-md text-onSurfaceVariant mb-4">
          <Trans i18nKey="about.block3.p4" components={boldComponent} />
        </p>
        <p className="text-body-md text-onSurfaceVariant mb-4">
          <Trans i18nKey="about.block3.p5" components={boldComponent} />
        </p>
        <p className="text-body-md text-onSurfaceVariant">
          <Trans i18nKey="about.block3.p6" components={boldComponent} />
        </p>
      </div>

      <div className={BOX_CLASS}>
        <BlockHeading icon={ShieldCheck}>
          {t("about.block4.title")}
        </BlockHeading>
        <ol className="space-y-3">
          {commitments.map((_item, i) => (
            <li key={i} className="flex gap-3">
              <span className="text-label-lg text-primary shrink-0">{i + 1}.</span>
              <p className="text-body-md text-onSurfaceVariant">
                <Trans i18nKey={`about.block4.items.${i}`} components={boldComponent} />
              </p>
            </li>
          ))}
        </ol>
      </div>
    </div>
  );
}
