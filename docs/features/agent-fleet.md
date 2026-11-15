# Agent42 agent fleet

A planner designs a team of agents for your task, the team runs, agents can hand sub-tasks to sub-teams of their own, and a judge reviews the result. It has its own page at `/fleet/`, linked from the sidebar as an icon button with the tooltip "Agent42 agent fleet".

## Run a task

1. Open the fleet page and describe what you want done.
2. Optionally pick tools with the composer's **Tools** picker and add files with **Attach files** (or drop them on the composer).
3. Send it. The planner designs a team and picks a pattern (Sequential, Concurrent, GroupChat, Handoff, Magentic or Staged).
4. Approve the plan if you are asked, answer any questions, and watch the team work.

Teams up to a configured depth ask you to approve their plan. Permission requests and questions from MCP servers appear the same way: a bar at the top with buttons and a text field.

## What to expect

- **Recursive.** Agents can design and run a new team for a sub-task. Depth and a soft budget keep this bounded.
- **Judged.** The result is scored 1 to 10. Below the passing score, the same team fixes it, then a new team builds on it. The best result is kept.
- **Your models only.** The planner, judge and moderators run on the model you pick, and agents can use any of your tool-capable models. With no provider, the page shows the same "add a provider" gate as the chat.
- **Tools per task.** Agents get web search and your MCP tools as your chat default selects them. Change the **Tools** picker to override that for one task. The task header shows "Tools: your defaults" or "Tools: N selected". Run again keeps the selection.
- **Bounded.** Every task has a token cap and an agent cap. You run one task at a time, and the operator caps tasks across all users.
- **Persistent.** Reload the page at any moment and it replays the task. A task that was running when the app restarted shows as **interrupted** and can be run again.

## Attached files

Attached files land in the task's workspace, where every agent can read them. Documents (PDF, Word, PowerPoint, Excel, text) are also indexed so agents can search and read them. Without an embedding model the files are still there but nothing is searchable. Follow-ups share the files, and Run again copies them into its new workspace.

## Canvas

Agents write named, versioned pages (html, svg, markdown or code) to a shared canvas. A **Canvas** card under the task lists them while it runs and opens them in the same sandboxed pane as chat artifacts. An open page reloads at each new version, and a page keeps its latest 20 versions.

## Graph view

A Details | Graph switch above the task shows teams as boxes and agents as nodes (pending, working, done, failed). Click a node for its output. The choice is remembered in your browser.

## Follow up and delete

A done task can be followed up: a new team gets the earlier requests, results and workspace. **Delete task** removes a task that is not running, with its log. The workspace and its files go once no follow-up shares them. Workspace files are listed under the task and download as attachments.

## Start a task from a chat

With the `fleet` tool on, a chat agent can start a fleet task for you. Nobody is asked: the plan and every Copilot permission are approved automatically (refusals outside the workspace still apply) and MCP questions are declined. Each automatic answer is listed under **Answered automatically**. The chat gets a link to the task, and when the task finishes the conversation gets a background turn with the result. Incognito chats can't start one.

## Copilot agents

A team can include Copilot agents. They run GitHub Copilot's loop with built-in shell, file and URL tools inside the task's workspace. Every write, command and fetch asks for your approval ("always for this task" is per task). Reads and writes outside the workspace are refused. The operator can switch Copilot agents off with `Fleet:CopilotAgentsEnabled`. After you sign in with GitHub, a Copilot agent can also run on your Copilot plan.

!!! note
    A shell can reach things on the server it runs on, so only run Copilot agents on a deployment you trust.

## Lessons

The **Lessons** button opens what the fleet learned about team design from your finished tasks. Once a night a model reads your recent finished tasks and keeps a few lessons that several tasks agree on. This is **off until you switch it on**, because it spends your own tokens. Each lesson is added to the prompt of every planner in your next tasks, and a task shows "Lessons applied (n)".

The view shows each lesson with its topic, confidence and sources. Lessons expire after 90 days without support. **Disable** keeps a lesson so it isn't proposed again, and delete removes it. **Dream now** runs it on demand. It needs a few finished tasks before it does anything.

## Editable prompts

The planner, judge, moderator and other prompts can be edited per user. Usage counts under the application "Agent42 fleet" in the usage dashboard.
