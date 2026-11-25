# Configuration health

> A read-only tab that shows which features are fully set up, degraded or off.

## What it is

The **Configuration** tab in Preferences lists the optional pieces of your deployment and whether each
one is configured, so a missing key doesn't fail silently. For example, it tells you when address lookup
is off because the Google Maps key is missing, or when Web Push is off because no VAPID key is set.

The tab is meant for the person running the instance. It is shown to **owners** only. Anyone else sees
an empty list. The report never shows a secret value, only a status and a short message.

## Read the report

Each row has a status:

| Status | Meaning |
|--------|---------|
| ok | Configured and in use |
| warning | Optional feature is off or incomplete, and the app still works |
| error | Something is set but wrong, or a live check failed |
| skipped | Not checked |
| n/a | Doesn't apply to this deployment |

## Run live checks

By default the tab only checks that settings are present and well formed. Press **Run live checks** to
also test that the web search and geocoding services actually respond. The geocoding check calls
Google Maps, so it can be billed.

!!! note
    A web search service that answers 403 is considered healthy. That just means its raw JSON API is
    switched off.

## Embedding models are not listed here

Embedding models are chosen per user, so they don't appear in this report. Check your own under
**Preferences → Model providers**. See [Document Q&A](document-qa.md).

## Self-hosted installs

With local storage, the report adds a storage provider row, and the Azure-only items (blob endpoint,
tenant id) show as not applicable. See [Self-hosting](self-hosting.md).

## Sign-in rows

An `Auth:Mode` row says whether Easy Auth or the app's own Entra sign-in is in use. With `entra`, two more rows appear:
`Auth:DataProtectionKeyRing` is an error when the key ring is not persisted (every restart then signs everyone out and
loses stored provider keys), and `Auth:OwnersLocalDev` is an error when `Access:Owners` lists `local-dev`. See
[Docker with Entra sign-in](../setup/docker-entra.md).

## For operators

Owners are the object ids listed in the `Access:Owners` setting. If it is empty, nobody is an owner and
the tab stays empty for everyone.
