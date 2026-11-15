# MCP Apps

A tool on one of your own MCP servers can ship an interactive view (the MCP Apps extension, `io.modelcontextprotocol/ui`). When
you turn **show this server's interactive apps** on for that server, the view appears inline in the chat bubble, under the tool
chip.

## What a view can do

- Show the tool's result and follow your theme, with a full-screen button.
- Call tools on **its own server** (`tools/call`), and read its own `ui://` resources.
- Open a link, only on a click and only `http` or `https`.
- Send a message as you, or tell the assistant something, **only after you confirm** in a dialog that shows the exact words.

## What protects you

- The view runs in a sandboxed frame with an opaque origin: no cookies, no storage, no access to `/api`.
- The page's CSP header limits where it can connect and load from to the web addresses its server declared (https and wss only;
  never this app, private addresses or wildcards that are too broad). That is an egress path to whatever the view holds, which
  is why it is a per-server switch. New servers start with it on; existing ones keep it off until you tick it.
- After a reload the card is **inactive** until you click *Activate app*, so a view cannot re-run a server tool on its own.
- Incognito chats show the card but store nothing. Public share links and the Android app show only the plain tool chip.

## For developers

Decisions and traps: ADR 0133 and the four MCP Apps entries in `.agentic/gotchas.md`. Offline, `dotnet run --project
tools/Agent42.DevHost` seeds a demo server whose tool `demo_show_app` renders a fixture view; ask the canned model to
"show the chart app". (With only .NET SDK 11 installed, build DevHost with `-p:StaticWebAssetsEnabled=false`.)
