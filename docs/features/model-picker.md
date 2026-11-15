# Model picker

Choose which model answers a conversation, and switch models mid-conversation without losing history.

## Pick a model

Use the model dropdown in the composer. Models are grouped by provider, and each group is labelled with the provider's name. The list comes from the providers you configured; see [Bring your own models](bring-your-own-models.md). A new provider appears in the picker without a restart.

If you have no providers, the picker is empty and the composer is disabled until you add one.

## Switch mid-conversation

Change the dropdown and send your next message. The whole conversation carries over to the new model. A conversation reopens on the model it last used.

If you switch to a model that can't use tools, earlier tool calls are dropped from what that model sees, so it isn't confused by content it can't handle. Your visible history is unchanged.

## Good to know

- **Agent chats** use the model pinned to the agent, so the picker doesn't change what answers there.
- **Reminders and background replies** run on the default model rather than the one you last picked.
- If a provider is removed after you chose one of its models, Agent42 falls back to your default model instead of failing.
