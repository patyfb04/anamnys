/* eslint-disable @typescript-eslint/no-unused-vars */
import { i18nBuilder } from 'keycloakify/login';
import type { ThemeName } from '../kc.gen';

/** @see: https://docs.keycloakify.dev/features/i18n */
const { useI18n, ofTypeI18n } = i18nBuilder
  .withThemeName<ThemeName>()
  .withCustomTranslations({
    // Anamnys-specific copy that doesn't map to any built-in Keycloak message key —
    // reusing e.g. `loginTitleHtml` here would squeeze an unrelated full sentence
    // into a small caption. Mirrors apps/provider's `t("auth.secureBadge")`.
    en: {
      secureBadge: 'Secure & Private',
      footerCopyright: '© 2026 Anamnys. Secure and private by default.',
      // Register.tsx's hero subhead — not doRegister (the button label, "Register"),
      // which read as a literal duplicate of the headline right above it.
      registerSubhead: 'Create your account and start documenting your clinical sessions.',
      // Template.tsx's header, mirroring apps/web's MarketingHeader.
      navProduct: 'Product',
      navPricing: 'Pricing & Plans',
      navAboutUs: 'About us',
      navSignIn: 'Sign in',
      navGetStarted: 'Get started',
    },
    // The realm's defaultLocale/supportedLocales (keycloak/realms/anamnys-providers.json)
    // is pt-BR, matching every other app in this repo — Keycloak's own built-in message
    // keys (email, password, doRegister, etc.) already ship pt-BR translations in
    // Keycloakify's default bundle; only these custom keys need one here.
    'pt-BR': {
      secureBadge: 'Seguro e Privado',
      footerCopyright: '© 2026 Anamnys. Seguro e privado por padrão.',
      registerSubhead: 'Crie sua conta e comece a documentar suas sessões clínicas.',
      navProduct: 'Produto',
      navPricing: 'Preços e Planos',
      navAboutUs: 'Sobre nós',
      navSignIn: 'Entrar',
      navGetStarted: 'Começar',
    },
  })
  .build();

type I18n = typeof ofTypeI18n;

export { useI18n, type I18n };
