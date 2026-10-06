import { useState } from 'react';
import { getKcClsx } from 'keycloakify/login/lib/kcClsx';
import type { UserProfileFormFieldsProps } from 'keycloakify/login/UserProfileFormFieldsProps';
import type { LazyOrNot } from 'keycloakify/tools/LazyOrNot';
import type { JSX } from 'keycloakify/tools/JSX';
import { Template } from '../Template';
import RequiredActionFooter from '../components/RequiredActionFooter';
import type { KcContext } from '../KcContext';
import type { I18n } from '../i18n';

type Props = {
  kcContext: Extract<KcContext, { pageId: 'update-email.ftl' }>;
  i18n: I18n;
  UserProfileFormFields: LazyOrNot<(props: UserProfileFormFieldsProps) => JSX.Element>;
  doMakeUserConfirmPassword: boolean;
};

// The email field comes from UserProfileFormFields for the same reason as Register.tsx:
// the realm's declarative user profile decides the field and its input name.
export default function UpdateEmail({ kcContext, i18n, UserProfileFormFields, doMakeUserConfirmPassword }: Props) {
  const { url, messagesPerField, isAppInitiatedAction } = kcContext;
  const { msg } = i18n;
  const [isFormSubmittable, setIsFormSubmittable] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const { kcClsx } = getKcClsx({
    doUseDefaultCss: true,
    classes: {
      kcInputClass: 'border border-outlineVariant rounded-radii-sm px-3 py-2 !w-full box-border',
    },
  });

  const globalError = messagesPerField.exists('global') ? messagesPerField.get('global') : null;

  return (
    <Template kcContext={kcContext} i18n={i18n} headline={msg('updateEmailTitle')} subhead={msg('updateEmailSubhead')}>
      <h2 className="text-headline-md text-[24px] text-onSurface mb-1">{msg('updateEmailTitle')}</h2>
      <p className="text-body-md text-onSurfaceVariant mb-5">{msg('updateEmailSubhead')}</p>

      {globalError && (
        <div className="bg-errorContainer rounded-[10px] p-2.5 mb-4">
          <p className="text-onErrorContainer text-[13px]">{globalError}</p>
        </div>
      )}

      <form id="kc-update-email-form" action={url.loginAction} method="post" onSubmit={() => setSubmitting(true)}>
        <div className="mb-4">
          <UserProfileFormFields
            kcContext={kcContext}
            i18n={i18n}
            kcClsx={kcClsx}
            onIsFormSubmittableValueChange={setIsFormSubmittable}
            doMakeUserConfirmPassword={doMakeUserConfirmPassword}
          />
        </div>
        <RequiredActionFooter
          i18n={i18n}
          submitting={submitting}
          submitDisabled={!isFormSubmittable}
          isAppInitiatedAction={isAppInitiatedAction}
        />
      </form>
    </Template>
  );
}
