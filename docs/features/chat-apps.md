# Chat apps

Talk to Agent 42 from your own bot on a chat app. Telegram, Slack and Discord work today. Signal is built but off until the operator turns it on, and WhatsApp is planned.

## Connect a bot

Each user connects their own bot.

1. Create a bot on the platform and copy its token.
2. In Agent 42, open Preferences, then **Chat apps**, and paste the token.
3. Send the bot the pairing code Agent 42 shows.

For platform-specific setup steps, see the [chat apps setup guide](../integrations/chat-apps/README.md).

## Chat with it

Direct messages to your bot become conversations with Agent 42, and they are listed in the sidebar like any other chat. Replies appear by editing one message in place as they stream.

These commands work in the chat: `/new`, `/stop`, `/model`, `/agent`, `/sethome` and `/status`, plus `/help`. On Slack, start them with `!` instead of `/`, because Slack keeps a leading `/` for itself.

- Send images and documents with a message. A file that can't be read is skipped with a note.
- Images the model generates come back as uploads.
- Voice notes are transcribed with your own transcription model.
- In groups, the bot answers you only when mentioned.

## Notifications

With notifications on, results of scheduled prompts and agent runs, and the agent's own notifications, are delivered to the chat the conversation came from. Use `/sethome` in a chat to make it your home chat for deliveries.

## Who can talk to it

Only the paired owner can use the bot. Everyone else, other bots included, is ignored without a reply.

## If a connection fails

If a token is dead, or another program is polling the same bot, the connection shows a reason in the Chat apps panel. Fix it and save the connection again to retry.

## Privacy

Agent 42 connects out to the chat platform, so you don't need to open a public address. Conversations you have through a chat app are stored by that platform under your own bot. Replies to chat apps are opt-in per connection.
