# Thin image over upstream SearXNG with our config baked in.
#
# Why baked and not mounted: `search.formats` lives in the `search:` section, which the
# container's SEARXNG_* env overrides do not cover (they target `server`/`general`), and
# mounting a settings.yml into Azure Container Apps would need an Azure Files volume.
#
# Tag pinned to the immutable tag whose digest matched `latest` on 2026-07-12; SearXNG ships
# rolling date tags, so `latest` is never what we deploy.
FROM searxng/searxng:2026.7.12-c19d86faa

COPY settings.yml /etc/searxng/settings.yml
