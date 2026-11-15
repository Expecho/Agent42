# Scheduled reminders

The assistant can schedule a prompt to run later, even when you're not watching. When it fires, the assistant replies in that conversation, so the chat moves forward on its own.

## Set a reminder

Switch on the **Scheduling** tool (see [Tool selection](tool-selection.md)), then ask in plain language:

- "Remind me to stand up every 30 minutes."
- "Check in on me every morning."
- "Summarize this conversation in an hour."

The assistant confirms when the reminder first fires and how often it repeats. Check that before you rely on it.

## When it fires

The stored instruction is sent to the assistant as if you had written it, and the answer is added to the conversation. If you're away and have push notifications set up, you can get a notification. See [Push notifications](push-notifications.md).

Reminders survive restarts, and each belongs to the conversation it was set in.

## Limits

- The shortest delay and the shortest repeat interval are **one minute**.
- Reminders **always repeat**. There is no true one-time reminder. For a single reminder, the assistant picks a long interval and tells you it keeps repeating until cancelled, so ask it to cancel afterward.
- A reminder runs without you, so it can't ask you questions or use your device location.

## Change or cancel

Ask the assistant to cancel a reminder by name, or to clear all of them. Reusing a name updates that reminder instead of adding a duplicate. Deleting the conversation removes its reminders.

## Privacy

A reminder turn sends the same content to your model provider as a normal turn. If [Long-term memory](long-term-memory.md) is on, reminder turns are included, even though you aren't present.
