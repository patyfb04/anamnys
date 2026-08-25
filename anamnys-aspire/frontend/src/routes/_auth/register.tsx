import { useEffect, useState } from "react";
import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { Trans, useTranslation } from "react-i18next";
import { IdCard, Mail, Lock } from "lucide-react";
import { useAuthStore } from "@/lib/store/authStore";
import { isPasswordStrong } from "@/lib/password";
import Button from "@/components/ui/Button";
import TextField from "@/components/ui/TextField";
import Checkbox from "@/components/ui/Checkbox";
import PasswordRequirementsList from "@/components/PasswordRequirementsList";

export const Route = createFileRoute("/_auth/register")({
  component: RegisterPage,
});

// This build is scoped to mental health practitioners only, so the account specialty is
// implicit rather than a choice on the sign-up form.
const SPECIALTY = "mental_health" as const;

function RegisterPage() {
  const navigate = useNavigate();
  const { t } = useTranslation();
  const [name, setName] = useState("");
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [confirmPassword, setConfirmPassword] = useState("");
  const [agreed, setAgreed] = useState(false);
  const { register, isSubmitting, error, clearError, user } = useAuthStore();

  useEffect(() => {
    clearError();
  }, [clearError]);

  useEffect(() => {
    if (user) navigate({ to: "/patients" });
  }, [user, navigate]);

  const handleRegister = async () => {
    clearError();
    if (!name || !email || !password) {
      alert(t("auth.register.missingFieldsBody"));
      return;
    }
    if (!isPasswordStrong(password)) {
      alert(t("auth.register.weakPasswordBody"));
      return;
    }
    if (password !== confirmPassword) {
      alert(t("auth.register.passwordMismatchBody"));
      return;
    }
    if (!agreed) {
      alert(t("auth.register.agreementRequiredBody"));
      return;
    }
    await register(email, password, name, SPECIALTY);
  };

  return (
    <>
      <h2 className="text-headline-md text-[24px] text-onSurface mb-1">{t("auth.register.formTitle")}</h2>
      <p className="text-body-md text-onSurfaceVariant mb-5">{t("auth.register.formSubtitle")}</p>

      {error && (
        <div className="bg-errorContainer rounded-[10px] p-2.5 mb-4">
          <p className="text-onErrorContainer text-[13px]">{error}</p>
        </div>
      )}

      <TextField
        label={t("auth.register.fullNameLabel")}
        icon={IdCard}
        placeholder={t("auth.register.fullNamePlaceholder")}
        value={name}
        onChange={(e) => setName(e.target.value)}
        containerClassName="mb-3.5"
      />
      <TextField
        label={t("auth.register.emailLabel")}
        icon={Mail}
        placeholder={t("auth.register.emailPlaceholder")}
        autoCapitalize="none"
        type="email"
        value={email}
        onChange={(e) => setEmail(e.target.value)}
        containerClassName="mb-3.5"
      />
      <TextField
        label={t("auth.register.passwordLabel")}
        icon={Lock}
        placeholder={t("auth.register.passwordPlaceholder")}
        secureToggle
        value={password}
        onChange={(e) => setPassword(e.target.value)}
        containerClassName="mb-3.5"
      />
      {password.length > 0 && <PasswordRequirementsList password={password} />}
      <TextField
        label={t("auth.register.confirmPasswordLabel")}
        icon={Lock}
        placeholder={t("auth.register.confirmPasswordPlaceholder")}
        secureToggle
        value={confirmPassword}
        onChange={(e) => setConfirmPassword(e.target.value)}
        containerClassName="mb-3.5"
      />

      <Checkbox checked={agreed} onToggle={() => setAgreed((a) => !a)} className="mt-1 mb-4">
        <span className="text-body-md text-onSurfaceVariant leading-5">
          <Trans
            i18nKey="auth.register.agreement"
            components={{
              terms: <span className="text-primary font-bold" />,
              privacy: <span className="text-primary font-bold" />,
            }}
          />
        </span>
      </Checkbox>

      <Button title={t("auth.register.submit")} onClick={handleRegister} loading={isSubmitting} rounded="md" />
    </>
  );
}
