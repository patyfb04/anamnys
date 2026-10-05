import { useState, type FormEvent, type ReactNode } from "react";
import { createFileRoute } from "@tanstack/react-router";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import Button from "@anamnys/shared/ui/Button";
import TextField from "@anamnys/shared/ui/TextField";
import { toast } from "@anamnys/shared/ui/Toaster";
import AccountPage from "@anamnys/shared/components/AccountPage";
import ProfileEmailRow from "@anamnys/shared/components/ProfileEmailRow";
import { useAuthStore } from "@anamnys/shared/lib/store/authStore";
import { profileApi, ProfileValidationError, type ProviderProfile } from "@anamnys/shared/api/profile";

export const Route = createFileRoute("/_app/account/profile")({ component: ProfilePage });

function ProfilePage() {
  const { t } = useTranslation();
  const { data } = useQuery({ queryKey: ["profile", "provider"], queryFn: profileApi.getProvider });

  return (
    <AccountPage title={t("accountMenu.profile")} subtitle={t("accountPages.profile.subtitle")}>
      {data && <ProfileForm key={data.email} initial={data} />}
    </AccountPage>
  );
}

function ProfileForm({ initial }: { initial: ProviderProfile }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const loadUser = useAuthStore((s) => s.loadUser);
  const [name, setName] = useState(initial.name);
  const [crpNumber, setCrpNumber] = useState(initial.crpNumber ?? "");
  const [crpRegion, setCrpRegion] = useState(initial.crpRegion ?? "");
  const [errors, setErrors] = useState<Record<string, string[]>>({});

  const save = useMutation({
    mutationFn: () => profileApi.updateProvider({ name, crpNumber: crpNumber || null, crpRegion: crpRegion || null }),
    onMutate: () => setErrors({}),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["profile", "provider"] });
      toast.success(t("accountPages.profile.saved"));
      await loadUser("provider");
    },
    onError: (error) => {
      if (error instanceof ProfileValidationError) setErrors(error.errors);
      else toast.error(t("accountPages.profile.saveError"));
    },
  });

  const onSubmit = (e: FormEvent) => {
    e.preventDefault();
    save.mutate();
  };

  return (
    <form onSubmit={onSubmit} noValidate>
      <ProfileEmailRow realm="provider" email={initial.email} />
      <Field error={errors.name?.[0]}>
        <TextField label={t("accountPages.profile.name")} value={name} onChange={(e) => setName(e.target.value)} />
      </Field>
      <div className="grid sm:grid-cols-2 gap-4">
        <Field error={errors.crpNumber?.[0]}>
          <TextField
            label={t("accountPages.profile.crpNumber")}
            value={crpNumber}
            onChange={(e) => setCrpNumber(e.target.value)}
          />
        </Field>
        <Field error={errors.crpRegion?.[0]}>
          <TextField
            label={t("accountPages.profile.crpRegion")}
            value={crpRegion}
            onChange={(e) => setCrpRegion(e.target.value)}
          />
        </Field>
      </div>
      <div className="mt-2">
        <Button type="submit" title={t("accountPages.profile.save")} loading={save.isPending} fullWidth={false} rounded="md" />
      </div>
    </form>
  );
}

function Field({ error, children }: { error?: string; children: ReactNode }) {
  return (
    <div className="mb-4">
      {children}
      {error && <p className="mt-1 text-body-md text-error">{error}</p>}
    </div>
  );
}
