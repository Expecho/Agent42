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

## Slack

Agent42 connects to Slack over **Socket Mode**: it opens the connection itself, so the app needs no request URL and
nothing in Slack points at Agent42.

1. At [api.slack.com/apps](https://api.slack.com/apps), choose **Create New App → From a manifest**, pick your
   workspace, and paste this manifest (YAML). It turns on Socket Mode, lets you DM the bot from its Messages tab, and
   asks only for the scopes below. You can rename the app.

   ```yaml
   display_information:
     name: Agent 42
   features:
     app_home:
       messages_tab_enabled: true
       messages_tab_read_only_enabled: false
     bot_user:
       display_name: Agent 42
       always_online: true
   oauth_config:
     scopes:
       bot:
         - app_mentions:read
         - channels:history
         - chat:write
         - files:read
         - groups:history
         - im:history
         - mpim:history
   settings:
     event_subscriptions:
       bot_events:
         - app_mention
         - message.channels
         - message.groups
         - message.im
         - message.mpim
     interactivity:
       is_enabled: false
     org_deploy_enabled: false
     socket_mode_enabled: true
     token_rotation_enabled: false
   ```

2. **Install to Workspace** (Settings → Install App). Copy the **Bot User OAuth Token**, which starts with `xoxb-`. A
   token starting `xoxp-` is a *user* token and is refused.
3. Under **Basic Information → App-Level Tokens**, choose **Generate Token and Scopes**, add the
   `connections:write` scope, and copy the token, which starts with `xapp-`. Socket Mode needs this second token.
4. In Agent42, open **Preferences → Chat apps → Connect a bot**, pick **Slack**, paste both tokens and press **Save**.
   Agent42 checks both with Slack first, and a rejected token saves nothing.
5. In Slack, open the app (under **Apps** in the sidebar), go to its **Messages** tab, and send the pairing code from
   the dialog. The app answers "Linked to Agent42".

**Commands start with `!` in Slack** (`!new`, `!stop`, `!help`), because Slack's message box treats a leading `/` as
one of its own slash commands. In a thread, the reply stays in the thread, and each thread is its own conversation.

| Symptom | Cause |
|---|---|
| "Slack also needs an app-level token" | Only the bot token was pasted. Generate the `xapp-` token (step 3). |
| The panel says "stopped: Socket Mode was turned off for this Slack app" | Socket Mode was switched off under **Settings → Socket Mode**. Turn it on, then press **Save**. |
| The panel says "stopped: Slack rejected the app-level token" | The `xapp-` token was revoked or lacks `connections:write`. Generate a new one and paste both tokens. |
| You can't type in the app's Messages tab | The manifest's `messages_tab_read_only_enabled` was set to true. Allow messages under **App Home**. |
| Someone else messages the app | Ignored, by design. Only the paired account can talk to it. |

## Discord

Agent42 connects to Discord's **gateway** (an outgoing websocket). It asks only for unprivileged intents, so you
don't need to switch on **Message Content Intent**: Discord sends a bot the text of its DMs and of messages that
mention it without it.

1. In the [Discord Developer Portal](https://discord.com/developers/applications), choose **New Application** and name
   it.
2. Open **Bot**, choose **Reset Token**, and copy the token. Treat it as a password.
3. You can only DM a bot you share a server with, or one you added to your own apps. Under **Installation**, either
   add a **User Install**, or use the **Guild Install** link with the `bot` scope and the **Send Messages**, **Send
   Messages in Threads**, **Create Public Threads** and **Read Message History** permissions to invite it to a server
   you are in.
4. In Agent42, open **Preferences → Chat apps → Connect a bot**, pick **Discord**, paste the token and press **Save**.
5. Open a DM with the bot in Discord and send the pairing code from the dialog. The bot answers "Linked to Agent42".

Commands start with `/` (`/new`, `/stop`, `/help`). The bot registers no slash commands of its own, so Discord sends the
text through as an ordinary message. Replies can't ping anyone: every message goes out with mentions switched off, so
`@everyone` in a reply is plain text.

| Symptom | Cause |
|---|---|
| The panel says "stopped: Discord rejected the bot token" | The token was reset in the Developer Portal. Paste the new one. |
| The panel says "stopped: Discord refused the bot's gateway intents" | Discord turned down the intents the bot asked for. Check the bot's settings in the Developer Portal, then press **Save**. |
| You can't open a DM with the bot | You don't share a server with it and haven't added it to your apps (step 3). |
| A message sent while Agent42 was restarting got no answer | Discord doesn't replay missed messages to bots. Send it again. |

**Groups (coming in phase 1c):** in a server channel the bot answers only its paired owner, only when mentioned, and in
a thread it starts on that message. Because the bot doesn't hold the Message Content intent, a message in that thread
also needs a mention (a reply that pings the bot counts).

## WhatsApp, Signal

Planned (ADR 0111, phases 2 and 3). Each will get a section here when it ships.

**No page here contains a secret value.**
