# Remote MCP servers

Connect your own remote HTTP MCP (Model Context Protocol) servers, and their tools become available to the agent with no extra setup.

## Add a server

Open **Preferences → MCP servers** and add a server. Choose how it authenticates:

- **None**
- **Static header**: a header name and value sent with every request.
- **Client credentials**
- **Interactive OAuth**: you sign in through the provider. The operator must register Agent42's callback address in the provider's app registration first.
- **Local bridge**: for a server that runs only on your own machine (see below).

Secrets are stored encrypted. Each server can be turned on or off individually in the tool picker, or all at once with the `mcp` entry.

## Test a connection

Press **Test** in the server dialog. It connects, lists the tools, and reports how many it found. It also runs after a successful save and when you return from an OAuth sign-in. A test that finds tools makes open chats pick them up. A failed test changes nothing.

## Reconnect

If an interactive OAuth sign-in expires, the chat shows a **Reconnect** button. It opens the sign-in in a new tab.

## Per-server options

- **Let links from this server be opened and read** is off by default. When on, web links in this server's tool results can be opened and read by the assistant like links you typed.
- **Show this server's interactive apps** controls inline views. See [MCP Apps](mcp-apps.md).
- A server can ask you for input mid-call. An open chat shows a form, or a link that opens only on a click and shows its host. Runs with nobody to ask (background, reminder, agent-run, Copilot) answer "cancel".

## Local bridge

Some MCP servers, like BrowserOS neo, listen only on `127.0.0.1`, which Agent42 can't reach. A small helper on your computer connects out to Agent42 and relays tool calls, so nothing on your machine is exposed.

1. Save the server with the **Local bridge** auth mode (no endpoint). The dialog shows a **bridge key once** and the command to run.
2. Download the helper for your platform from the Agent42 GitHub Releases (look for `agent42-local-bridge-<version>-<platform>`: Windows, macOS and Linux, x64 and arm64). It is a single executable, no .NET install needed. It isn't code-signed: on Windows choose *More info → Run anyway*, and on macOS run `xattr -d com.apple.quarantine Agent42.LocalBridge` once.
3. Set `AGENT42_BRIDGE_KEY` to the key and run `./Agent42.LocalBridge --server https://chat.agent42.nl` (use your own Agent42 address).
4. Press **Test**. It reports the tool count, or "local bridge not connected" while the helper is off.

Useful options: `--upstream <url>` if the local server isn't at `http://127.0.0.1:9210/mcp`, `--auto-approve tool,tool` to skip prompts for named tools, `--yolo` to skip **every** prompt, and `--version`.

For any tool the server hasn't declared read-only, the helper asks you in its console, showing the arguments. Declining, no answer within 60 seconds, or arguments too large to review all deny the call. Answer `a` or `always` to allow that tool for the helper's session. Read-only tools (page text, screenshots, browsing history) run without asking, and their output goes to the model, so narrow the server's tools and keep link reading off.

**Regenerate key** rotates the key and disconnects the running helper.

## The Chrome extension

Instead of the helper, the **Agent42 Chrome extension** can act as the bridge and drive your real Chrome profile with your sign-ins.

1. Save a **Local bridge** server as above and copy the key.
2. Download `agent42-chrome-extension-<version>.zip` from the Agent42 GitHub Releases, unzip it into a folder you keep, open `chrome://extensions`, turn on **Developer mode** and choose **Load unpacked**.
3. In the extension's options, enter your Agent42 address and paste the key. The badge reads `on` when connected, `key` when the key was rejected (regenerate and paste again) and `set` while not configured.
4. Press **Test** in Agent42. It lists the extension's 14 tools.

Actions that change a page open a small approval window naming the tab, with *Allow once*, *Always allow this tool* (until Chrome closes) or *Deny*. No answer within 60 seconds is a deny. Know what it allows:

- The agent can act on **every open tab**.
- **`evaluate` runs page JavaScript without asking.**
- Agent runs, API keys, reminders and inbound email can reach it, not only chats you watch.

Put sensitive sites on the options page's **blocked list** (nothing runs there; the Agent42 site is blocked by default), and narrow the bridge server's tools for agents others can reach. The extension never acts on its own pages or browser-internal pages. While it drives a tab, Chrome shows a "being debugged" banner. Dismissing it stops the extension on that tab until you close the tab.

## Images from MCP tools

When an MCP tool returns an image (such as a screenshot), a vision model sees it for the current turn only. The conversation stores a short placeholder, so the model takes a new screenshot if it needs one. A model without vision, a Copilot chat and a fleet run get "image omitted". At most the latest 3 images per turn are sent, each up to 1 MiB.
