# Skill: Reminders

You can schedule reminders for the current conversation with the `create_reminder` tool.
A reminder is a **scheduled prompt**: when it fires, its prompt is sent to you as a user
message in this conversation, and you act on it — your response is saved in the
conversation history for the user to see.

## When to use it

Call `create_reminder` whenever the user asks to be reminded about something, wants a
recurring nudge, or wants you to do something later — for example "remind me to stand up
every 30 minutes", "check in on me every morning", or "summarize this conversation in an
hour".

## How reminders behave

- They are backed by Orleans reminders, so they are **durable**: a reminder survives the
  agent being deactivated and the silo restarting, and it belongs to this conversation.
- When a reminder fires, its prompt arrives as a user message **while the user may be
  away**; answer it as a normal turn, addressed to the user, so it reads well when they
  come back.
- A reminder first fires after an initial delay and then **repeats on a fixed interval**;
  Orleans reminders always recur, there is no true one-shot.
- The smallest granularity Orleans supports is **one minute**, so both the initial delay
  and the repeat interval must be at least 1 minute. If the user asks for something
  faster, explain that one minute is the minimum.

## Arguments to provide

- `name` — a short, stable identifier such as `stand-up` or `daily-check-in`. Reusing an
  existing name **updates** that reminder instead of creating a duplicate.
- `prompt` — the instruction you will receive when the reminder fires. Write it as a
  self-contained instruction to your future self, e.g. "Remind the user to stand up and
  stretch." or "Write a short summary of the conversation so far."
- `dueInMinutes` — how long from now the reminder should first fire (must be ≥ 1).
- `repeatEveryMinutes` — how often it repeats after the first firing (must be ≥ 1). For a
  reminder the user wants only once, use a large interval and tell them it will keep
  repeating until it is cancelled.

## After scheduling

Confirm back to the user exactly what you scheduled — when it will first fire, how often
it repeats, and what will happen when it does. Never claim a reminder was set unless the
tool call actually succeeded; if the tool returns an error message, relay it and ask the
user how to adjust.
