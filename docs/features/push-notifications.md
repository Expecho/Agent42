# Push notifications

> Get a notification on your phone when an agent replies while you're away.

Works in the installed app on Android or Chromium (see [PWA install & sign-in](pwa-and-sign-in.md)).

## What it is

There are two kinds of notification:

| | Away reply | Assistant-written |
|---|---|---|
| Who decides | The app, automatically | The model, when it judges something worth interrupting you for |
| Text | The agent's name and "Replied while you were away." | Written by the model |
| When | A [scheduled reminder](scheduled-reminders.md) fires or an external agent run finishes | Any time, including during a live chat |
| Setting | Always on once you subscribe | The **push_notification** tool in **Preferences → Tools** |

Tapping a notification opens the conversation. If the assistant already sent one during a turn, you
won't also get the away-reply for the same answer.

## Turn it on

1. Install Agent42 as an app and open it.
2. Enable notifications from the notifications toggle in the app's preferences, and allow them when
   your device asks.
3. Repeat on each device you want notifications on.

If the toggle isn't there, push is not set up on your server (see below).

## Limits and things to know

- The assistant can send up to 3 notifications per turn.
- It can only say a notification was **queued**, not that it arrived. Push services give no receipt.
- It won't send one in an incognito chat, or if you have no subscribed device.
- Notifications are always shown, even if the app is open.
- A device that stops working is removed automatically.

## Privacy

Notifications travel through your device vendor's push service (Google, Mozilla or Microsoft) and are
encrypted end to end. The away-reply text is just the agent's name. Assistant-written notifications can
contain anything from the conversation, and because a model writes them, content it read elsewhere could
influence the wording on your lock screen.

## For operators

Push needs a VAPID key pair: set the `VAPID_PRIVATE_KEY` secret and the matching public key. Without
them the app runs normally and the toggle is hidden.
