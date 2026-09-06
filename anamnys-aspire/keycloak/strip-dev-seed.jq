# Removes dev-only realm content before Keycloak's importer ever sees the file:
# the seeded local login (`.users`) and every localhost:* origin each SPA's
# Vite dev-server proxy needs registered (`redirectUris` and the pipe-delimited
# `post.logout.redirect.uris` attribute). Templated ${ANAMNYS_APP_ORIGIN}
# entries are left untouched — those are the real, environment-supplied origin.
del(.users)
| .clients |= map(
    (if has("redirectUris") then
      .redirectUris |= map(select(contains("localhost") | not))
     else . end)
    | (if (has("attributes") and (.attributes | has("post.logout.redirect.uris"))) then
      .attributes["post.logout.redirect.uris"] |=
        (split("##") | map(select(contains("localhost") | not)) | join("##"))
     else . end)
  )
