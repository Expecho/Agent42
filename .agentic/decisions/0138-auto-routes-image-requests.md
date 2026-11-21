---
id: 0138
date: 2026-11-21
status: accepted
tags: [models, routing, image-generation, egress, per-user, no-migration]
supersedes: []
extends: [0020]
amends: [0137]
---

# Title: Auto routes a picture request to the user's image-generation model, as a one-off turn

## Context
The Auto router (ADR 0137) chose only among chat models: its candidate filter dropped every non-chat model, so "draw a cat"
under Auto was answered in text, and a conversation left on an image model was moved to a chat model by its first Auto turn.
Auto was meant to remove manual model picking, and pictures were the one case it could not cover. An image turn is not a chat
turn (ADR 0020): picking an image model switches the conversation to it and the next text message would hit the image endpoint,
so routing to one cannot reuse the chat switch.

## Decision
- **Image models join Auto.** Every declared image-generation model whose provider can build an image service (only
  OpenAI-compatible ones do) is an Auto option. No per-model flag. The router's instructions say to pick one only when the user
  asks to create, draw, generate or edit a picture. Describing or reading one stays a vision chat model. This amends ADR 0137's
  "non-chat models are excluded" line. Embedding, speech, transcription and copilot-only models stay excluded.
- **A picture is a one-off turn.** The turn runs on a per-turn image model id. The conversation's model, agent, `DeploymentName`
  and `AutoModel` flag are untouched, and the next text turn runs on the chat model as before. `Routed` (id and name) is always
  emitted for it, because it tells the user why they got a picture.
- **The current model is never an image model.** When the conversation sits on an image model, the router is told "no current
  model", so a chat winner switches off it with no margin. A turn that does not route (router failure, null decision, no router,
  incognito) on such a conversation runs on the user's default chat model, never the picture endpoint. A turn skipped
  for another run in flight cannot switch (ADR 0106) and stays where it is.
- **A higher bar.** An image option wins only at `ImageMinProbability` (0.8, against 0.6 for a chat switch) AND as the largest
  answer, "cannot tell" included. Below that, ADR 0137's sticky rule runs over the chat options only. When no chat model survives the
  capability filter (an attached image and no vision model, tools on and no tools model), nothing is routed and no call is made,
  however many image models remain; a lone chat candidate is still chosen without a call. The numbers are starting points to tune in the live check, not measured values.
- **Edit input is narrow.** An Auto picture edits this turn's attached image, else the picture the IMMEDIATELY preceding turn
  made, else nothing (text-to-image). It never reuses an older image. Picking the image model by hand keeps ADR 0020's "most
  recent image anywhere".
- **Follow-ups route back.** The router's input gains one fixed line when the previous reply was a picture (read from the
  assistant message's image id key, not the ref), so "make it bluer" can choose the image model again.
- **No fallback on failure.** If routing picks the image model and generation fails, the error surfaces as an explicit image
  turn's does and nothing is recorded. There is no automatic chat reply.
- **Egress (extends ADR 0013 and 0137).** An Auto picture turn sends the prompt, and for an edit the input image's BYTES, to the
  image model's provider, which the user did not pick for this chat. The worst case is a misrouted turn that carries an
  attachment, which is why the bar is higher, image models are never routed without a chat alternative, and the edit input is limited to the
  attached or immediately preceding image. Skips are unchanged: incognito turns, agent-pinned chats and overlapping runs do not
  route.

## Alternatives considered
- **A per-model "use in Auto" flag** — consistent with ADR 0137's rejected per-model opt-out: a provider the user does not want
  used must not be connected.
- **Reuse "most recent image anywhere" for Auto edits** — "draw a dog" could silently become an edit of an old photo, and old
  photos would be sent to an image provider unprompted.
- **A chat-model tool that makes images** — rejected in ADR 0020; the model would need to decide and the image provider would
  still see the prompt.
- **One shared threshold for chat and image options** — a wrong picture costs money and a wrong chat reply does not.
- **Switch the conversation to the image model, as a hand pick does** — flips the ongoing chat model, so the next text message
  would hit the image endpoint.
- **Fall back to a chat reply when generation fails** — double-spends and hides the failure.

## Consequences
- Auto can now make and edit pictures. The picker stays on Auto and the conversation keeps its chat model.
- Image generation is not in the usage dashboard (it is not an `IChatClient`), so Auto picture turns add no usage row and no
  estimated cost.
- A user photo followed by "make it a cartoon" in a LATER message has no input (the previous turn was chat): attach it again. A
  regenerated Auto picture turn removes the last turn first, so it edits the picture before it, where the original may have been
  text-to-image.
- The two thresholds are tuned in the live check against a real Foundry deployment, and the numbers go in the PR.
- No persisted-state change: `RouterCandidate`, `RouterTurn` and `RouterDecision` are in-process records, and the grain keeps no
  new field (ADR 0106).
