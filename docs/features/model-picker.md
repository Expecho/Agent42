# Model picker

Choose which model answers a conversation, and switch models mid-conversation without losing history.

## Pick a model

Use the model dropdown in the composer. Models are grouped by provider, and each group is labelled with the provider's name. The list comes from the providers you configured; see [Bring your own models](bring-your-own-models.md). A new provider appears in the picker without a restart.

If you have no providers, the picker is empty and the composer is disabled until you add one.

## Auto

If you added an [Auto router](bring-your-own-models.md#auto-router), the picker has an **Auto** entry at the top. With Auto selected, each message is first sent to the router, which picks one of your other models for it. The reply shows a small label, "auto → model name", next to the sender. Auto is sticky: it keeps the model the conversation is on unless the router is clearly sure another one fits better.

Auto only chooses among models that can do the job: for a message with images it considers vision models only, and it never picks a model that can't use tools while tools are on.

Auto can also make pictures. If you added an image-generation model, Auto offers it when you ask it to create, draw or edit a picture (not to describe one), and it needs to be more sure of that than of a normal switch. That one message runs on the image model and the conversation stays on its chat model, so your next message is answered in text again. An Auto picture edits only the image you attach to that message or the picture the previous message made, never an older one in the conversation (attach the photo again if you need that). Picking the image model yourself keeps working as before. If the image model fails, you see the error and Auto does not fall back to a text reply. If the router is unreachable, the message just runs on the current model. In chat apps, `/model auto` does the same.

## Switch mid-conversation

Change the dropdown and send your next message. The whole conversation carries over to the new model. A conversation reopens on the model it last used.

If you switch to a model that can't use tools, earlier tool calls are dropped from what that model sees, so it isn't confused by content it can't handle. Your visible history is unchanged.

## Good to know

- **Agent chats** use the model pinned to the agent, so the picker doesn't change what answers there. Auto is not offered there, and incognito chats don't use it either, so a picture request there stays with the model you picked.
- **Reminders and background replies** run on the default model rather than the one you last picked.
- If a provider is removed after you chose one of its models, Agent42 falls back to your default model instead of failing.
