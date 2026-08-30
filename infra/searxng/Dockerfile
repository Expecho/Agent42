# Thin image over upstream SearXNG with our config baked in.
#
# Why baked and not mounted: `search.formats` lives in the `search:` section, which the
# container's SEARXNG_* env overrides do not cover (they target `server`/`general`), and
# mounting a settings.yml into Azure Container Apps would need an Azure Files volume.
#
# Tag pinned to the immutable tag whose digest matched `latest` on 2026-08-30; SearXNG ships
# rolling date tags, so `latest` is never what we deploy.
#
# Bumped from 2026.7.12-c19d86faa for the Google engine ALONE. On the old tag Google shipped
# `inactive: true`, which makes `load_engines` skip it entirely — `keep_only` cannot bring it back and
# no override in settings.yml can reach it. Upstream repaired the engine and downgraded it to
# `disabled: true`, which settings.yml CAN override.
#
# Bing was only ever `disabled`, so settings.yml alone enables it — measured, the `engines:` override
# restores web_search on the OLD tag too. Search does not depend on this bump; revert it alone if ever
# needed and the override keeps working, minus Google. See ADR 0060.
FROM searxng/searxng:2026.8.29-d226b78bc

COPY settings.yml /etc/searxng/settings.yml
