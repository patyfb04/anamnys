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
import { profileApi, ProfileValidationError, type PatientProfile } from "@anamnys/shared/api/profile";

export const Route = createFileRoute("/_app/account/profile")({ component: ProfilePage });

function ProfilePage() {
  const { t } = useTranslation();
  const { data } = useQuery({ queryKey: ["profile", "patient"], queryFn: profileApi.getPatient });

  return (
    <AccountPage title={t("accountMenu.profile")} subtitle={t("accountPages.profile.subtitle")}>
      {data && <ProfileForm key={data.email ?? ""} initial={data} />}
    </AccountPage>
  );
}

function ProfileForm({ initial }: { initial: PatientProfile }) {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const loadUser = useAuthStore((s) => s.loadUser);
  const [firstName, setFirstName] = useState(initial.firstName);
  const [lastName, setLastName] = useState(initial.lastName);
  const [phone, setPhone] = useState(initial.phone ?? "");
  const [dateOfBirth, setDateOfBirth] = useState(initial.dateOfBirth ?? "");
  const [errors, setErrors] = useState<Record<string, string[]>>({});

  const save = useMutation({
    mutationFn: () =>
      profileApi.updatePatient({
        firstName,
        lastName,
        phone: phone || null,
        dateOfBirth: dateOfBirth || null,
      }),
    onMutate: () => setErrors({}),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: ["profile", "patient"] });
      toast.success(t("accountPages.profile.saved"));
      await loadUser("patient");
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
      <ProfileEmailRow realm="patient" email={initial.email} />
      <div className="grid sm:grid-cols-2 gap-4">
        <Field error={errors.firstName?.[0]}>
          <TextField
            label={t("accountPages.profile.firstName")}
            value={firstName}
            onChange={(e) => setFirstName(e.target.value)}
          />
        </Field>
        <Field error={errors.lastName?.[0]}>
          <TextField
            label={t("accountPages.profile.lastName")}
            value={lastName}
            onChange={(e) => setLastName(e.target.value)}
          />
        </Field>
      </div>
      <div className="grid sm:grid-cols-2 gap-4">
        <Field error={errors.phone?.[0]}>
          <TextField
            label={t("accountPages.profile.phone")}
            type="tel"
            value={phone}
            onChange={(e) => setPhone(e.target.value)}
          />
        </Field>
        <Field error={errors.dateOfBirth?.[0]}>
          <TextField
            label={t("accountPages.profile.dateOfBirth")}
            type="date"
            value={dateOfBirth}
            onChange={(e) => setDateOfBirth(e.target.value)}
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
