# Long-term memory

With memory on, the assistant remembers facts across conversations ("my daughter is called...", "I prefer metric units"). Before each reply it recalls what it knows about you, and after each turn it saves what was said.

Memory is kept per user, in a private store on the third-party Hindsight service.

## Turn it on

Two switches must both be on.

1. **The operator** sets the `HINDSIGHT_API_KEY` secret. Without it, nothing is remembered, nothing is sent to Hindsight, and the tool doesn't show up in the picker.
2. **You** switch on **Long-term memory** in **Preferences → Tools**, in the composer's **Tools** popover, or in a custom agent's tools. It is on by default if you have never pressed Save in the tool list. If you saved earlier, turn it on and save again. See [Tool selection](tool-selection.md).

## What it does

- It works across all your chats, in the background. You don't ask for it.
- It works with any model, including text-only ones.
- The operator can optionally change the memory's focus (`Hindsight:Mission`) or point it at a self-hosted Hindsight server (`Hindsight:BaseAddress`).

## When it is not used

- **Incognito chats** don't recall or save anything.
- Image-generation turns don't use it.
- Agents called from outside through the agent MCP endpoint don't use it, so a key holder can't read or write your memories.
- A continuous agent chat created before memory existed stays without it.

## Privacy

!!! note
    While memory is on, every normal turn sends your message and the assistant's reply to Hindsight, and sends your message again to look up memories. That includes whatever the turn carried: document excerpts, a street address, tool and MCP results. Reminder and agent-run turns count too, and they run when you aren't there.

!!! note
    **There is no way to delete memories.** Turning memory off stops future recall and saving, but what is already stored stays. If something wrong or malicious was saved, it can come back in later chats. Only enable it if you are comfortable with that.

## Check that it's configured

**Preferences → Configuration** shows whether the key is present ("configured; each user selects it per chat") or missing ("off; the agent recalls and retains nothing"). It never shows the key itself.
