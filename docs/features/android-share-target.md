# Android Share sheet

> Share text, links, images or documents from other Android apps straight into Agent42.

Works only with the installed app on Android or Chromium. iOS does not allow this.

## What it is

Once you install Agent42 as an app (see [PWA install & sign-in](pwa-and-sign-in.md)), it shows up in
Android's **Share** sheet. Share something to it, choose where it goes, and the content is placed in
the message box. Nothing is sent until you press send.

## Share something to Agent42

1. In any app, tap **Share** and choose **Agent42**.
2. Agent42 opens and asks where to send it: a **new chat** or one of your agents.
3. The text or link is filled into the composer. Images and documents are attached the same way as
   when you attach them yourself.
4. Add a question if you like, review, and send.

You can share text, links, images, PDF, Word, PowerPoint, Excel, plain text and Markdown files.
Images are handled as [image attachments](image-attachments.md) and documents as
[document Q&A](document-qa.md), so documents need an embedding model set up first.

## Tips

- Cancel the picker to drop the shared content.
- Reloading the page doesn't share it a second time.
- Be careful sharing very large files into a long-running agent, since they add to its conversation
  size. See [Custom agents](custom-agents.md).

## If it doesn't work

The share only works once the app's background service is active. If you installed very recently, open
Agent42 once and try again.
