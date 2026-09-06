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
    },
  })
  .build();

type I18n = typeof ofTypeI18n;

export { useI18n, type I18n };
