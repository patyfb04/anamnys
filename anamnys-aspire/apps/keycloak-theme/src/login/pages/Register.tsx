import { useState } from 'react';
import { getKcClsx } from 'keycloakify/login/lib/kcClsx';
import type { UserProfileFormFieldsProps } from 'keycloakify/login/UserProfileFormFieldsProps';
import type { LazyOrNot } from 'keycloakify/tools/LazyOrNot';
import type { JSX } from 'keycloakify/tools/JSX';
import Button from '@anamnys/shared/ui/Button';
import { Template } from '../Template';
import type { KcContext } from '../KcContext';
import type { I18n } from '../i18n';

type Props = {
  kcContext: Extract<KcContext, { pageId: 'register.ftl' }>;
  i18n: I18n;
  UserProfileFormFields: LazyOrNot<(props: UserProfileFormFieldsProps) => JSX.Element>;
  doMakeUserConfirmPassword: boolean;
};

// A native form post to Keycloak's url.registrationAction, not a controlled React form
// calling an API — same reasoning as Login.tsx, Keycloak owns the credential exchange.
// UserProfileFormFields (Keycloakify's own component, not hand-rolled TextFields like
// Login.tsx's fixed username/password pair) renders whatever fields this realm's
// declarative user profile actually configures and names its inputs correctly for
// Keycloak's registration action — hand-rolling field names here would risk silently
// mismatching them.
export default function Register({ kcContext, i18n, UserProfileFormFields, doMakeUserConfirmPassword }: Props) {
  const { url } = kcContext;
  const { msg, msgStr } = i18n;
  const [isFormSubmittable, setIsFormSubmittable] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  // kcInputClass override, not a hand-rolled TextField (see the comment above): matches
  // TextField.tsx's own border-outlineVariant token, so these fields read as the same
  // input style used everywhere else in the app.
  const { kcClsx } = getKcClsx({
    doUseDefaultCss: true,
    classes: {
      kcInputClass: 'border border-outlineVariant rounded-radii-sm px-3 py-2 !w-full box-border',
    },
  });

  return (
    <Template kcContext={kcContext} i18n={i18n} headline={msg('registerTitle')} subhead={msgStr('registerSubhead')}>
      <h2 className="text-headline-md text-[24px] text-onSurface mb-1">{msg('registerTitle')}</h2>

      <form
        id="kc-register-form"
        action={url.registrationAction}
        method="post"
        onSubmit={() => setSubmitting(true)}
        className="mt-3.5"
      >
        <UserProfileFormFields
          kcContext={kcContext}
          i18n={i18n}
          kcClsx={kcClsx}
          onIsFormSubmittableValueChange={setIsFormSubmittable}
          doMakeUserConfirmPassword={doMakeUserConfirmPassword}
        />

        <Button
          type="submit"
          title={msgStr('doRegister')}
          loading={submitting}
          disabled={!isFormSubmittable}
          rounded="md"
          className="mt-4"
        />
      </form>

      <a href={url.loginUrl} className="block text-center mt-4.5 text-label-lg text-primary">
        {msg('backToLogin')}
      </a>
    </Template>
  );
}
