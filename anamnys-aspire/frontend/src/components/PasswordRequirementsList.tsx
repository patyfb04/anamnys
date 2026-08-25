import { CheckCircle2, Circle } from "lucide-react";
import { useTranslation } from "react-i18next";
import { getPasswordRequirements } from "@/lib/password";

export default function PasswordRequirementsList({ password }: { password: string }) {
  const { t } = useTranslation();
  const requirements = getPasswordRequirements(password);

  return (
    <div className="flex flex-col gap-1 mt-2 mb-1.5">
      {requirements.map((req) => (
        <div key={req.key} className="flex items-center gap-1.5">
          {req.met ? (
            <CheckCircle2 size={14} className="text-tertiary" />
          ) : (
            <Circle size={14} className="text-outline" />
          )}
          <span className={`text-label-md ${req.met ? "text-tertiary" : "text-onSurfaceVariant"}`}>
            {t(`common.passwordRequirements.${req.key}`)}
          </span>
        </div>
      ))}
    </div>
  );
}
