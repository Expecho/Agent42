# Image attachments

> Attach images to a message so a vision-capable model can see them.

## What it is

You can attach one or more images to a message. If the model you picked supports vision, it looks at
the images and answers about them. If it doesn't, it is told it can't see them, and it won't pretend to.

## Attach an image

1. In the composer, use the attach button (or paste or drop an image).
2. Wait for the thumbnail to appear, then type your question.
3. Send. The image goes along with that message.

Images stay with the conversation, so you can refer back to them in later messages.

## Switching models mid-conversation

If you switch from a vision model to one without vision, the older images are held back from the new
model. They are not deleted. Switch back to a vision model and it can see them again.

## Privacy

- Images are stored privately and are only readable by you, within your own conversation.
- When you send a message, the model provider you picked receives the image so it can look at it.
- Images are not shown to anyone else unless you share the conversation.

## Self-hosted installs

If you run Agent42 yourself with local storage, images live on the server's volume instead of Azure
Storage, and are sent to the model with each request. Images over 4 MB, or more than 16 MB of images
in one request, are replaced by a short text note (oldest first). See [Self-hosting](self-hosting.md).

## Related

- Documents (PDF, Office, text) are not image attachments. See [Document Q&A](document-qa.md).
- To create or edit pictures, see [Image generation](image-generation.md).
