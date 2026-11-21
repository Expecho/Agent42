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

## Auto router

The **Auto router** (kind **Auto router (Microsoft Decision-1)**) lets Agent 42 pick a model for each message, so you don't have to. It needs a Microsoft Decision-1 deployment on your own Azure AI Foundry resource, which you deploy yourself.

1. Add a provider and choose the kind **Auto router (Microsoft Decision-1)**.
2. **endpoint**: your Foundry resource root, for example `https://my-resource.services.ai.azure.com`. A project URL is cut back to this.
3. **api key**: the key of that resource.
4. **model**: exactly one, the name of your Decision-1 deployment.

The router is never a chat model. It adds an **Auto** entry to the [model picker](model-picker.md#auto). Without this provider there is no Auto and nothing is sent anywhere.

To steer it, fill in **when to use** on your other models, for example "long documents and careful reasoning". Without a hint, Agent 42 describes a model from its public model card when it can find one, and otherwise from its capabilities, so the router works with no setup.

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

With **Auto**, the text of your message is also sent to your own Decision-1 deployment, and the conversation then goes to whichever of your models it picks, including providers outside your organization. If you don't want a provider used, don't connect it, or pick models by hand. Agent 42 also fetches a public list of model descriptions (one request with nothing about you in it) to describe your models to the router.
