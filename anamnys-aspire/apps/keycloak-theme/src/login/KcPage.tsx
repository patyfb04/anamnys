import { Suspense, lazy } from 'react';
import type { ClassKey } from 'keycloakify/login';
import type { KcContext } from './KcContext';
import { useI18n } from './i18n';
import DefaultPage from 'keycloakify/login/DefaultPage';
// The stock keycloakify Template (with its default CSS) backs every page this theme does
// not override — only login.ftl, login-otp.ftl, login-reset-password.ftl, register.ftl
// and login-verify-email.ftl get the Anamnys shell (./Template).
import DefaultTemplate from 'keycloakify/login/Template';

const UserProfileFormFields = lazy(() => import('keycloakify/login/UserProfileFormFields'));
const Login = lazy(() => import('./pages/Login'));
const LoginOtp = lazy(() => import('./pages/LoginOtp'));
const LoginResetPassword = lazy(() => import('./pages/LoginResetPassword'));
const Register = lazy(() => import('./pages/Register'));
const LoginVerifyEmail = lazy(() => import('./pages/LoginVerifyEmail'));

const doMakeUserConfirmPassword = true;

export default function KcPage(props: { kcContext: KcContext }) {
  const { kcContext } = props;

  const { i18n } = useI18n({ kcContext });

  return (
    <Suspense>
      {(() => {
        switch (kcContext.pageId) {
          case 'login.ftl':
            return <Login kcContext={kcContext} i18n={i18n} />;
          case 'login-otp.ftl':
            return <LoginOtp kcContext={kcContext} i18n={i18n} />;
          case 'login-reset-password.ftl':
            return <LoginResetPassword kcContext={kcContext} i18n={i18n} />;
          case 'register.ftl':
            return (
              <Register
                kcContext={kcContext}
                i18n={i18n}
                UserProfileFormFields={UserProfileFormFields}
                doMakeUserConfirmPassword={doMakeUserConfirmPassword}
              />
            );
          case 'login-verify-email.ftl':
            return <LoginVerifyEmail kcContext={kcContext} i18n={i18n} />;
          default:
            return (
              <DefaultPage
                kcContext={kcContext}
                i18n={i18n}
                classes={classes}
                Template={DefaultTemplate}
                doUseDefaultCss={true}
                UserProfileFormFields={UserProfileFormFields}
                doMakeUserConfirmPassword={doMakeUserConfirmPassword}
              />
            );
        }
      })()}
    </Suspense>
  );
}

const classes = {} satisfies { [key in ClassKey]?: string };
