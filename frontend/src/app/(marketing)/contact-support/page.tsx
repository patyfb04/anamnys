"use client";

import { useState } from "react";
import Image from "next/image";
import { useTranslation } from "react-i18next";
import { Mail, MapPin, Send, ShieldCheck } from "lucide-react";
import TextField from "@/components/ui/TextField";
import Button from "@/components/ui/Button";
import AlertMessage from "@/components/ui/AlertMessage";
import { contactApi } from "@/api/contact";

// Matches specs/UI/Contact/screen.png: centered header, then a 12-col grid — an 8-col contact
// form card and a 4-col sidebar (Direct Contact info + a secure/private-handling callout — no
// HIPAA naming, this product is LGPD/Brazil-focused, not US healthcare). Submits to
// POST /api/contact (ClinicalDraft.Api's ContactController) — unauthenticated, since this page
// is reachable by anyone, logged in or not.
export default function ContactSupportPage() {
  const { t } = useTranslation();
  const [fullName, setFullName] = useState("");
  const [email, setEmail] = useState("");
  const [phone, setPhone] = useState("");
  const [message, setMessage] = useState("");
  const [sent, setSent] = useState(false);
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [missingFields, setMissingFields] = useState(false);

  const handleSubmit = async () => {
    setError(null);
    if (!fullName.trim() || !email.trim() || !message.trim()) {
      setMissingFields(true);
      return;
    }
    setIsSubmitting(true);
    try {
      await contactApi.submit(fullName.trim(), email.trim(), phone.trim(), message.trim());
      setSent(true);
      setFullName("");
      setEmail("");
      setPhone("");
      setMessage("");
    } catch (e) {
      setError(e instanceof Error ? e.message : t("contact.sendFailedBody"));
    } finally {
      setIsSubmitting(false);
    }
  };

  return (
    <div className="max-w-6xl mx-auto px-4 md:px-12 py-12 md:py-16">
      <AlertMessage
        open={missingFields}
        onOpenChange={setMissingFields}
        title={t("contact.missingFieldsTitle")}
        description={t("contact.missingFieldsBody")}
      />

      <div className="text-center mb-10">
        <h1 className="text-headline-lg text-[28px] md:text-[32px] text-primary mb-2">{t("contact.title")}</h1>
        <p className="text-body-lg text-onSurfaceVariant max-w-2xl mx-auto">{t("contact.subtitle")}</p>
      </div>

      <div className="grid grid-cols-1 lg:grid-cols-12 gap-6">
        {/* Contact form */}
        <div className="lg:col-span-8 bg-surfaceContainerLowest border border-outlineVariant rounded-radii-xl p-6 shadow-lg shadow-primary/5">
          {sent && (
            <div className="bg-secondaryFixed rounded-radii-md p-3 mb-5">
              <p className="text-label-lg text-onSecondaryFixed">{t("contact.sentTitle")}</p>
              <p className="text-body-md text-onSecondaryFixedVariant mt-0.5">{t("contact.sentBody")}</p>
            </div>
          )}
          {error && (
            <div className="bg-errorContainer rounded-radii-md p-3 mb-5">
              <p className="text-body-md text-onErrorContainer">{error}</p>
            </div>
          )}
          <div className="grid grid-cols-1 md:grid-cols-2 gap-4 mb-4">
            <TextField
              label={t("contact.fullName")}
              placeholder={t("contact.fullNamePlaceholder")}
              value={fullName}
              onChange={(e) => setFullName(e.target.value)}
            />
            <TextField
              label={t("contact.email")}
              type="email"
              placeholder={t("contact.emailPlaceholder")}
              value={email}
              onChange={(e) => setEmail(e.target.value)}
            />
          </div>
          <div className="mb-4">
            <div className="flex justify-between items-center mb-1.5">
              <label className="text-label-sm text-onSurfaceVariant">{t("contact.phone")}</label>
              <span className="text-body-md italic text-onSurfaceVariant">{t("contact.phoneOptional")}</span>
            </div>
            <TextField
              type="tel"
              placeholder={t("contact.phonePlaceholder")}
              value={phone}
              onChange={(e) => setPhone(e.target.value)}
            />
          </div>
          <TextField
            label={t("contact.message")}
            placeholder={t("contact.messagePlaceholder")}
            value={message}
            onChange={(e) => setMessage(e.target.value)}
            multiline
            rows={5}
            containerClassName="mb-5"
          />
          <div className="flex justify-end">
            <Button
              title={t("contact.submit")}
              onClick={handleSubmit}
              loading={isSubmitting}
              icon={<Send size={18} />}
              fullWidth={false}
              rounded="md"
            />
          </div>
        </div>

        {/* Sidebar */}
        <div className="lg:col-span-4 flex flex-col gap-6">
          <div className="bg-surfaceContainerLow rounded-radii-xl p-6 border border-outlineVariant/50">
            <h3 className="text-headline-sm text-[18px] text-primary mb-4">{t("contact.directContact")}</h3>

            <div className="flex items-start gap-3.5 mb-4">
              <div className="w-10 h-10 rounded-full bg-primaryFixed flex items-center justify-center shrink-0">
                <Mail size={18} className="text-primary" />
              </div>
              <div>
                <p className="text-label-lg text-onSurface">{t("contact.supportEmail")}</p>
                <a href="mailto:support@clinicaldraft.ai" className="text-body-md text-primary hover:underline">
                  support@clinicaldraft.ai
                </a>
                <p className="text-label-md text-onSurfaceVariant mt-1 normal-case">{t("contact.supportEmailResponseTime")}</p>
              </div>
            </div>

            <div className="h-px w-full bg-outlineVariant/50 mb-4" />

            <div className="flex items-start gap-3.5">
              <div className="w-10 h-10 rounded-full bg-primaryFixed flex items-center justify-center shrink-0">
                <MapPin size={18} className="text-primary" />
              </div>
              <div>
                <p className="text-label-lg text-onSurface">{t("contact.headquarters")}</p>
                <address className="text-body-md text-onSurfaceVariant not-italic mt-1">Canadá</address>
              </div>
            </div>
          </div>

          {/* Secure/private callout — suport.jpg as the background instead of a flat color box */}
          <div className="relative rounded-radii-xl overflow-hidden border border-primary/20 min-h-55 flex flex-col items-center justify-center text-center p-6">
            <Image src="/suport.jpg" alt="" fill className="object-cover" />
            <div className="absolute inset-0 bg-linear-to-t from-primary/95 via-primary/70 to-primary/30" />
            <div className="relative z-10 flex flex-col items-center">
              <ShieldCheck size={36} className="text-onPrimary mb-2" />
              <h4 className="text-headline-sm text-[18px] text-onPrimary mb-1.5">{t("contact.secureTitle")}</h4>
              <p className="text-body-md text-onPrimary/90">{t("contact.secureBody")}</p>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
