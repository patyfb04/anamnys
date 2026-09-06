# Removes dev-only realm content before Keycloak's importer ever sees the file:
# the seeded local login (`.users`), the `anamnys-test-*` service-account
# clients whose secret is a literal committed to git, and every localhost:*
# origin each SPA's Vite dev-server proxy needs registered (`redirectUris`,
# `webOrigins`, and the "##"-delimited `post.logout.redirect.uris` attribute).
# It also tightens `sslRequired` to "all". Templated ${ANAMNYS_APP_ORIGIN}
# entries are left untouched — those are the real, environment-supplied origin.
# `webOrigins: ["+"]` (Keycloak's "same origins as redirectUris" wildcard) is
# also left untouched — only literal localhost entries are ever stripped.
del(.users)
# "external" (the dev value) lets Keycloak serve login and token endpoints over
# plain HTTP to any peer it considers private — which, behind a TLS-terminating
# ingress, is every peer. Dev runs the whole flow over plain http://localhost
# and so needs "external"; this filter is already the dev/prod discriminator,
# so it is also where the production value gets set. Spec §8 ("HTTPS
# throughout") is only true with this clause.
| .sslRequired = "all"
# anamnys-test-provider / anamnys-test-owner exist only so the scheme-isolation
# tests can mint real tokens (every real BFF client disables the password and
# client_credentials grants). Each carries "secret": "test-only-not-a-secret"
# — a value in git — plus serviceAccountsEnabled and an anamnys-api audience
# mapper, so leaving them in a published image means anyone who can read this
# repository can mint a providers-realm token the provider-bearer scheme
# accepts on /api/phi/*. DevOnlyTestClientGuard cannot substitute for this: it
# runs after Keycloak has already imported and activated the client.
| (if has("clients") then
    .clients |= map(select(.clientId | startswith("anamnys-test-") | not))
   else . end)
| .clients |= map(
    (if has("redirectUris") then
      .redirectUris |= map(select(contains("localhost") | not))
     else . end)
    | (if has("webOrigins") then
      .webOrigins |= map(select(. == "+" or (contains("localhost") | not)))
     else . end)
    | (if (has("attributes") and (.attributes | has("post.logout.redirect.uris"))) then
      .attributes["post.logout.redirect.uris"] |=
        (split("##") | map(select(contains("localhost") | not)) | join("##"))
     else . end)
  )
