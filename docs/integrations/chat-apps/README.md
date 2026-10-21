# Chat apps: setting up your bot

Each Agent42 user connects their **own** bot (ADR 0111). The operator sets up nothing: there is no secret to add to
GitHub Actions and no app to register. Everything below is done by the user, in the chat app and in Agent42.

## Telegram

1. In Telegram, open a chat with **@BotFather**, send `/newbot`, and follow the steps (a display name, then a username
   ending in `bot`).
2. BotFather replies with a token like `123456789:ABC…`. Treat it as a password.
3. In Agent42, open **Preferences → Chat apps → Connect a bot**, pick **Telegram**, paste the token and press **Save**.
   Agent42 checks the token with Telegram first; a rejected token saves nothing.
4. The dialog shows a **pairing code**, valid for 15 minutes. Open your new bot in Telegram, press **Start**, and send
   the code as a message. The bot answers "Linked to Agent42".
5. Send it anything. The conversation also appears in your Agent42 chat list.

| Symptom | Cause |
|---|---|
| The bot never answers the code | The code expired or was typed wrong five times. Press **New pairing code** in the dialog. |
| The panel says "stopped: Another program is reading this bot's messages" | Something else uses this bot token (Telegram allows one reader). Stop it, then press **Save** to retry. |
| The panel says "stopped: Telegram rejected the bot token" | The token was revoked in BotFather. Paste the new one. |
| Someone else messages your bot | Ignored, by design. Only the paired account can talk to it. |

**Groups (coming in phase 1c):** Telegram bots in privacy mode do not see plain `@mentions` in groups, only commands
addressed to them and replies to their messages. To use the bot in groups, turn privacy mode off in BotFather
(`/setprivacy`) or make the bot a group admin, and remove and re-add it to the group afterwards.

## Slack, Discord, WhatsApp, Signal

Planned (ADR 0111, phases 1b, 2 and 3). Each will get a section here when it ships.

**No page here contains a secret value.**
