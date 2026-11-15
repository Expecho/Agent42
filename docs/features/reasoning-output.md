# Reasoning output

When a model reasons before answering, its "thinking" streams into a collapsed section above the reply.

## What you see

While the model thinks, the reply gets a collapsed **Thinking…** section above the answer that fills in live. When the turn ends, it is labelled **Thought process** and stays collapsed. Click it to read.

The thinking is shown live only. It isn't saved, so after you reload the chat only the answer remains. It is also never read aloud in [voice mode](voice-and-speech.md), and it isn't used for follow-up suggestions.

## Turn it on for a model

In Preferences, open the model providers and edit the provider. Tick the **reasoning** checkbox on the model. This makes Agent42 ask the model for a reasoning summary.

The checkbox doesn't control display: a model that sends its thinking anyway still shows it, ticked or not.

!!! note
    Some providers (DeepSeek, for example) send reasoning without being asked. Others only send it when you tick the box and use an API that supports it.

## Privacy

Reasoning text stays out of the saved conversation, so it isn't stored and isn't sent back to the model on later turns.
