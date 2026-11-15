# Artifacts

The model can create and revise standalone pages, apps, SVG drawings, documents and code files. Each one appears as a card in the reply and opens in a side pane.

## What artifacts can be

| Kind | Opens as |
|---|---|
| HTML | a live page (scripts run, but it can't reach the network) |
| SVG | the drawing |
| Markdown | a rendered document, with diagrams, code, math and charts as in a reply |
| Code | highlighted source, with its language |

## Use them

1. Ask for something standalone: "make a small calculator page" or "write this as a document".
2. Click the card in the reply. It opens in the side pane.
3. Ask for changes. Each change is saved as a new version, and the pane's version picker steps through them.
4. Use the pane to copy or download the result.

The **Artifacts in this chat** button in the chat header lists every artifact in the conversation, including ones no longer visible in the history.

## Turn them on

Artifacts are a tool in the tool picker. If you saved a tool selection before they existed, switch the **Artifacts** tool on yourself. They are not available in incognito chats.

## Limits

- A version is capped at 64 KB, an artifact at 20 versions, and a chat at 20 artifacts. Content over the cap is refused rather than cut off.
- HTML artifacts run in a sandbox: they can load libraries from cdnjs and nothing else, and they can't read your Agent42 data.
- Shared chat links show artifacts too.

## Try it offline

Run the offline dev host (`dotnet run --project tools/Agent42.DevHost`) and send **make an artifact**. Then send **update the artifact** for version 2. **make a markdown artifact** and **make a code artifact** show the other kinds.
