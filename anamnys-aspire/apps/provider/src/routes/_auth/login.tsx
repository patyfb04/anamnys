import { useEffect, useState } from "react";
import { createFileRoute, useNavigate } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";
import { Mail, Lock, KeyRound, ArrowLeft, Fingerprint } from "lucide-react";
import { useAuthStore } from "@anamnys/shared/lib/store/authStore";
import Button from "@anamnys/shared/ui/Button";
import TextField from "@anamnys/shared/ui/TextField";

export const Route = createFileRoute("/_auth/login")({
  component: LoginPage,
});

function LoginPage() {
  const navigate = useNavigate();
  const { t } = useTranslation();
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");
  const [code, setCode] = useState("");
  const {
    login,
    completeTwoFactorLogin,
    cancelTwoFactor,
    pendingTwoFactor,
    isSubmitting,
    error,
    clearError,
    user,
  } = useAuthStore();

  useEffect(() => {
    clearError();
  }, [clearError]);

  useEffect(() => {
    if (user) navigate({ to: "/patients" });
  }, [user, navigate]);

  const handleLogin = async () => {
    clearError();
    if (!email || !password) {
      alert(t("auth.login.missingFieldsBody"));
      return;
    }
    await login(email, password);
  };

  const handleTwoFactorSubmit = async () => {
    clearError();
    if (!code.trim()) {
      alert(t("auth.login.twoFactor.missingCodeBody"));
      return;
    }
    await completeTwoFactorLogin(code.trim());
  };

  if (pendingTwoFactor) {
    return (
      <>
        <h2 className="text-headline-md text-[24px] text-onSurface mb-1">{t("auth.login.twoFactor.formTitle")}</h2>
        <p className="text-body-md text-onSurfaceVariant mb-5">{t("auth.login.twoFactor.subtitle")}</p>

        {error && (
          <div className="bg-errorContainer rounded-[10px] p-2.5 mb-4">
            <p className="text-onErrorContainer text-[13px]">{error}</p>
          </div>
        )}

        <TextField
          label={t("auth.login.twoFactor.codeLabel")}
          icon={KeyRound}
          placeholder={t("auth.login.twoFactor.codePlaceholder")}
          autoCapitalize="none"
          autoCorrect="off"
          value={code}
          onChange={(e) => setCode(e.target.value)}
          containerClassName="mb-3.5"
        />

        <Button title={t("auth.login.twoFactor.submit")} onClick={handleTwoFactorSubmit} loading={isSubmitting} rounded="md" className="mt-1" />

        <button
          onClick={() => {
            setCode("");
            cancelTwoFactor();
          }}
          className="w-full flex items-center justify-center gap-1.5 mt-4.5 text-label-lg text-primary"
        >
          <ArrowLeft size={18} />
          {t("auth.login.twoFactor.back")}
        </button>
      </>
    );
  }

  return (
    <>
      <h2 className="text-headline-md text-[24px] text-onSurface mb-1">{t("auth.login.formTitle")}</h2>
      <p className="text-body-md text-onSurfaceVariant mb-5">{t("auth.login.subtitle")}</p>

      {error && (
        <div className="bg-errorContainer rounded-[10px] p-2.5 mb-4">
          <p className="text-onErrorContainer text-[13px]">{error}</p>
        </div>
      )}

      <TextField
        label={t("auth.login.emailLabel")}
        icon={Mail}
        placeholder={t("auth.login.emailPlaceholder")}
        autoCapitalize="none"
        type="email"
        value={email}
        onChange={(e) => setEmail(e.target.value)}
        containerClassName="mb-3.5"
      />
      <TextField
        label={t("auth.login.passwordLabel")}
        icon={Lock}
        placeholder={t("auth.login.passwordPlaceholder")}
        secureToggle
        value={password}
        onChange={(e) => setPassword(e.target.value)}
        containerClassName="mb-3.5"
      />

      <Button title={t("auth.login.submit")} onClick={handleLogin} loading={isSubmitting} rounded="md" className="mt-1" />

      <button className="w-full flex items-center justify-center gap-1.5 mt-4.5 text-label-lg text-primary">
        <Fingerprint size={18} />
        {t("auth.login.biometric")}
      </button>
    </>
  );
}
