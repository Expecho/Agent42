# Image generation

> Pick an image model to generate a picture from a prompt, or edit an existing image.

## What it is

When you choose a model that can generate images, sending a message produces **an image instead of a
text reply**. It is a model you select, not a tool the chat model calls on its own.

## Generate an image

1. Add an image-capable model to one of your providers and mark it as an image generation model (see
   [Bring your own models](bring-your-own-models.md)).
2. Pick that model in the model picker.
3. Describe the picture and send. The image appears in the conversation.

Generated images are saved with the conversation, so they are still there when you reload.

## Edit an image

Attach an image (see [Image attachments](image-attachments.md)) and describe the change. If you don't
attach one, the model works from the most recent image in the conversation. With no image at all, it
creates a new one from your prompt.

## Going back to chat

Switch back to a normal chat model to talk about the picture. If that model has vision, it can see the
generated image; if not, the image is held back until you use a vision model.

## Tips

- Be specific about subject, style and composition.
- Iterate: each new message edits the latest image.

## Privacy

Your prompt and any input image are sent to the provider of the image model you picked.
