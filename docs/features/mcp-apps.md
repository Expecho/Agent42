# MCP Apps

A tool on one of your own MCP servers can ship an interactive view (the MCP Apps extension). When you turn **show this server's interactive apps** on for that server, the view appears inline in the chat bubble, under the tool chip.

## What a view can do

- Show the tool's result and follow your theme, with a full-screen button.
- Call tools on **its own server** and read its own resources.
- Open a link, only on a click and only `http` or `https`.
- Send a message as you, or tell the assistant something, **only after you confirm** in a dialog that shows the exact words.

## What protects you

- The view runs in a sandboxed frame: no cookies, no storage, no access to the app's own data.
- It can only connect to the web addresses its server declared (https and wss only; never this app, private addresses or overly broad wildcards). That is a path for data to leave, which is why it is a per-server switch. New servers start with it on. Existing servers keep it off until you tick it.
- After a reload the card is **inactive** until you click *Activate app*, so a view cannot re-run a server tool on its own.
- Incognito chats show the card but store nothing. Public share links and the Android app show only the plain tool chip.

## Try it offline

Run the offline dev host (see [Self-hosting](self-hosting.md) for the other ways to run Agent42). It seeds a demo server with a `demo_show_app` tool. Ask the canned model to "show the chart app".
