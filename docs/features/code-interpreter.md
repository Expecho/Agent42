# Code interpreter

The assistant can run Python to calculate, analyze data and produce results, instead of guessing at the answer.

## Requirements

The code runs in a sandbox hosted by the model provider, not on your machine or the Agent 42 server. That means it needs:

- A model on a provider that uses the Responses API. For Azure AI Foundry, turn on **use Responses API** when you add the provider. See [Bring your own models](bring-your-own-models.md).
- The **Code interpreter** tool switched on. See [Tool selection](tool-selection.md).

On other models the option is greyed out in the tool list.

## Use it

Ask for something that benefits from computing, such as "work out the compound interest on these numbers" or "analyze this CSV and summarize it". The assistant decides when to run code.

## Good to know

- On these models, the provider's own web search replaces the built-in one. You use it the same way. See [Web search](web-search.md).
- While code runs, the conversation waits for the result before the turn finishes.
- Models that can't use tools get none of this, even on a Responses API provider.
- Your data is processed in the model provider's sandbox, so treat it like any other content you send that provider.
