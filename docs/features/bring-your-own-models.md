# Bring your own models

Agent 42 ships with **no built-in chat model**. You connect your own model providers, and your keys stay with you.

## Add a provider

1. Open **Preferences → Model providers**.
2. Click **Add provider**.
3. Fill in the form:
    - **key**: a short name for this provider (no colon).
    - **kind**: **Azure AI Foundry**, **OpenAI-compatible** (Ollama, vLLM, GitHub Models and similar), or **Anthropic (Claude)**.
    - **display name**, **endpoint** and **api key**.
    - **use Responses API** (Foundry only): turn this on for models that need it. It also unlocks hosted tools such as the [code interpreter](code-interpreter.md).
    - **models**: declare each model the provider offers. Mark whether it supports **tools** and **vision**; leave them off if you are unsure.
4. Save.

Each model shows up in the model picker as `key:model`. Changes apply right away, with no restart.

Until you add at least one provider, the model picker is empty and the composer is locked.

## Choose defaults

- **Default model**: the model new chats start on.
- **Default embedding model**: used by [Document Q&A](document-qa.md). Mark a model as **embeddings** when you declare it. Without one, attaching documents is blocked.

You can still switch models per chat from the picker. See [Model picker](model-picker.md).

## Keys

Your API key is stored encrypted and is never shown again after saving. When you edit a provider, leave the key blank to keep the one already stored.

## Connect GitHub Copilot

If you have a GitHub Copilot subscription, click **Sign in with GitHub** on the same tab. Your Copilot models appear in the list.

- They work **only in Copilot agents**. Other pickers hide them.
- You still need an ordinary provider for plain chats and for the default model.
- Delete the row to disconnect.
- The button is hidden unless the operator has set up a GitHub OAuth App on the server. When self-hosting, see [Self-hosting](self-hosting.md#github-copilot).

## Privacy

Whichever model you pick receives that conversation's content: messages, document excerpts, image links and your location when you share it. If that provider is outside your own organization, the content leaves it. This is why picking a model is always your explicit choice.
