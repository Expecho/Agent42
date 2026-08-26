# Google Calendar setup

What you must do by hand in Google Cloud before the calendar tools (ADR 0053) work. Everything here
happens **outside this repository**; nothing in the code can do it for you.

Once this is done, each user connects their own Google account from **Preferences → Calendar**. The
setup below is per **deployment**; the connection is per **user**.

Budget about fifteen minutes. No prior Google Cloud knowledge is assumed.

> **Nothing on this page is a secret, and nothing on it should become one.** Name the setting, never
> its value — and no screenshots showing a client secret.

---

## 1. Create the project and enable the API

1. Go to <https://console.cloud.google.com/> and create a project (or pick an existing one). The name
   is only ever seen by you.
2. Open **APIs & Services → Library**, search for **Google Calendar API**, and press **Enable**.

Skipping this one is not subtle: every calendar call fails, and the tools report that the request
could not be completed.

---

## 2. Configure the OAuth consent screen — and set it to *In production*

Open **APIs & Services → OAuth consent screen**.

1. **User type: External.** (Internal is only available with Google Workspace, and only for your own
   organisation's accounts.)
2. Fill in the app name, a user support email and a developer contact email. The app name is what the
   user sees on the consent screen, so make it something they will recognise — "Agent 42".
3. **Publishing status → In production.** Press **Publish app** and confirm.

**This step is not optional, and it is the one most likely to be skipped.** While the consent screen
is in **Testing** status with an External user type, Google **revokes every refresh token after seven
days**. Your connection would work perfectly, then die a week later with no error anywhere — the
calendar simply stops answering, and you would reconnect every Monday without knowing why.

**What "In production" does *not* mean here.** The scopes below are *sensitive*, and this app is not
verified by Google, so users still see a **"Google hasn't verified this app"** interstitial and must
click **Advanced → Go to … (unsafe)** to continue. That is expected and safe for a personal
deployment of software you built yourself. Verification is only worth pursuing if you intend to hand
this to people who cannot vouch for the app.

---

## 3. Add exactly two scopes

Under **Data access** (or **Scopes** on the older console), add exactly:

| Scope | What it buys |
|---|---|
| `https://www.googleapis.com/auth/calendar.events` | Read **and write** events on **all** the user's calendars — the five event tools |
| `https://www.googleapis.com/auth/calendar.calendarlist.readonly` | Enumerate the user's calendars — `list_calendars`, and the account identity shown in Preferences |

**Do not use the broader `https://www.googleapis.com/auth/calendar`.** It adds exactly three things
this app deliberately does not do: creating calendars, deleting calendars, and changing who a
calendar is shared with. That distinction is load-bearing rather than tidy-minded — the calendar
tools write **without a per-action confirmation gate**, and prompt injection is an acknowledged
channel (a fetched web page, an uploaded skill). Under this pair, a successful injection can damage
*events*. Under the broad scope it could delete a whole calendar or grant a stranger write access to
one.

There is deliberately **no `openid` or `email` scope**. The connected account shown in Preferences is
the **primary calendar's id**, which for a Google account is the account address — so the identity
comes for free and needs no extra permission.

---

## 4. Create the OAuth client

**APIs & Services → Credentials → Create credentials → OAuth client ID.**

1. **Application type: Web application.** Not "Desktop app" — a Web application client is a
   *confidential* client, and that is what this app is built for.
2. Under **Authorized redirect URIs**, add exactly:

   ```
   https://chat.agent42.nl/api/google/oauth/callback
   ```

   Character for character. Google matches this **exactly**: a trailing slash, `http` instead of
   `https`, or a different host is a mismatch. If you also want to connect from a local DevHost run,
   add that origin's callback as a second URI (e.g.
   `http://localhost:61326/api/google/oauth/callback`).
3. Copy the **client ID** and **client secret** shown once on creation.

**Symptom of getting the URI wrong:** Google shows **`redirect_uri_mismatch`** before you ever reach
a consent screen. Compare the URI in the browser's address bar against the list in the console — they
differ somewhere.

---

## 5. Add the secrets and deploy

Both values are secrets. In the GitHub repository, under **Settings → Secrets and variables →
Actions**, add:

| Secret | Value |
|---|---|
| `GOOGLE_OAUTH_CLIENT_ID` | The client ID from step 4 |
| `GOOGLE_OAUTH_CLIENT_SECRET` | The client secret from step 4 |

Both are needed. A Google Web application client is confidential, so the token exchange carries the
secret as well as the PKCE verifier — setting only the ID leaves the connection broken at the last
step.

They are **optional to the deployment**: with neither set the app starts normally, Preferences →
Calendar says *not available*, and the calendar tools return a "not connected" notice per call. Push
to `main` to deploy, then check **Preferences → Configuration** — `GoogleCalendar:ClientId` and
`GoogleCalendar:ClientSecret` should both read *configured*.

Then open **Preferences → Calendar** and press **Connect**.

---

## 6. The step that is easy to miss: press Save in the Tools tab

**Open Preferences → Tools and press Save once.**

If you have *ever* saved a tool selection before — in Preferences → Tools, in a per-conversation Tools
popover, or on an agent — that stored selection is an explicit list of keys, and it cannot contain
`calendar`, because the key did not exist when you saved it. The tools will be invisible to the model
while everything else looks correct.

There is deliberately no migration: rewriting every stored selection to add a key the user never
chose is a worse precedent than one press of Save.

Tick **Calendar** on any stored agent that needs it, too — an agent snapshots its tool selection when
a chat starts.

---

## 7. Troubleshooting

| Symptom | Cause |
|---|---|
| `redirect_uri_mismatch` on the Google screen | The redirect URI in step 4 does not match exactly. Behind the Container Apps ingress this is usually an `http://` vs `https://` difference. |
| `invalid_client` | Wrong client ID or secret, or the secret was regenerated in the console and not updated in the GitHub secret. |
| Preferences says *"Google did not return a refresh token"* | The consent did not carry `access_type=offline` + `prompt=consent`, or Google reused an existing grant. Remove Agent 42 at <https://myaccount.google.com/permissions> and connect again. |
| **Worked for exactly a week, then stopped** | The consent screen is still in **Testing** status (step 2). Google revokes refresh tokens after 7 days. Publish the app and reconnect. |
| `403` / insufficient scope on some calls | The two scopes from step 3 are not both granted. Disconnect, remove the app at <https://myaccount.google.com/permissions>, and reconnect so the consent screen is shown again. |
| Preferences → Calendar shows *not available* | Neither secret reached the app. Check **Preferences → Configuration**. |
| The tools are never called, though everything reads *connected* | The **Tools** tab has not been re-saved — see step 6. |
| **The calendar went quiet after a restart, with no error anywhere** | The Data Protection key ring is not persisting out of process, so the stored refresh token can no longer be decrypted and every user reads as "not connected". This should not happen — the `dataprotection` blob container exists for exactly this — but it is the one failure with no visible error. Look for the "could not be decrypted" warning in the container logs; the remedy for the user is to reconnect. |

---

## 8. How to revoke

Two separate things, and doing one is not doing the other:

- **In the app** — Preferences → Calendar → **Disconnect**. This deletes the stored tokens. The
  calendar tools immediately report "not connected".
- **At Google** — <https://myaccount.google.com/permissions> → remove Agent 42. This withdraws the
  *grant itself*.

Disconnecting in the app does **not** revoke the grant at Google's end. Do both if you want the
permission gone rather than merely unused.

---

## What connecting actually permits

Worth being plain about, since the consent screen's wording is Google's rather than ours. A connected
account lets the assistant, at your request and **without asking you to confirm each action**:

- read the names, times, descriptions, locations and attendee counts of your events;
- list your calendars — including shared ones, whose ids are **other people's email addresses**;
- create, change and delete events on any of them.

Two consequences follow from that, both recorded in ADR 0053 rather than mitigated:

- **What a read returns enters the conversation**, and from there it is subject to everything else a
  conversation is subject to: it goes to whichever model is picked (which on a third-party model
  means leaving the Entra tenant), it is visible in a read-only share link, and it is mirrored
  unencrypted into the browser's IndexedDB for offline reading.
- **The tools are live on the asynchronous paths too** — a fired reminder, and `POST /api/agent-run`.
  A model-invoked delete can fire with no user present.

Neither is a reason not to use the feature. They are reasons to know what you connected.
