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
- **Tools per task.** Agents get web search, your skills, long-term memory (recall only, nothing a task does is remembered), past chats, push notifications (a tap opens the task) and your MCP tools as your chat default selects them, Copilot agents included. With an image model they can also generate images into the workspace's `images/` folder. Scheduling, location, browser tools, artifacts and code interpreter stay chat-only. Change the **Tools** picker to override that for one task. The task header shows "Tools: your defaults" or "Tools: N selected". Run again keeps the selection.
- **Bounded.** Every task has a token cap and an agent cap. You run one task at a time, and the operator caps tasks across all users.
- **Persistent.** Reload the page at any moment and it replays the task. A task that was running when the app restarted shows as **interrupted**. It can be **recovered** (see below) or run again.

## Attached files

Attached files land in the task's workspace, where every agent can read them. Documents (PDF, Word, PowerPoint, Excel, text) are also indexed so agents can search and read them. Without an embedding model the files are still there but nothing is searchable. Follow-ups share the files, and Run again copies them into its new workspace.

## Canvas

Agents write named, versioned pages (html, svg, markdown or code) to a shared canvas. A **Canvas** card under the task lists them while it runs and opens them in the same sandboxed pane as chat artifacts. An open page reloads at each new version, and a page keeps its latest 20 versions.

## Graph view

A Details | Graph switch above the task shows teams as boxes and agents as nodes (pending, working, done, failed). Click a node for its output. The choice is remembered in your browser.

## Recover an unfinished task

An interrupted, stopped or failed task offers **Recover** next to **Run again**. Run again starts the request over in a new workspace. Recover starts a new task that shares the old task's workspace and canvas, rebuilds the team you already approved (no planner call and no approval prompt, unless a model or tool it used is gone, in which case a new team is planned), and hands the agents a short digest of what was done: results, reviewer feedback, the todo list and each agent's last output. The digest is data, never instructions. The old task stays as history, and the new one sits under it in the task list (`recovered from #n`).

What it cannot do: an agent is never resumed mid-conversation, and a sub-team's work is not replayed, so the root team's agents start again with the digest. Agent output is kept only while the event log has room. Notes are saved after each attempt, so a task cut off during its first long attempt loses them. A recovered task keeps the old token cap, so a task stopped by the cap will probably hit it again. A task started from a chat asks for approvals on the page when recovered and does not report back to its chat.

## Follow up and delete

A done task can be followed up: a new team gets the earlier requests, results and workspace. In the sidebar's task list, follow-ups sit under the first task of their chain, and a thread moves to the top when it gets a new follow-up. A thread with a running or selected follow-up opens by itself, and its chip shows or hides the follow-ups. A follow-up that branches from an earlier step says which task it continues (`↳ #12`). **Delete task** removes a task that is not running, with its log. The workspace and its files go once no follow-up shares them. Workspace files are listed under the task and download as attachments.

## Start a task from a chat

With the `fleet` tool on, a chat agent can start a fleet task for you. Nobody is asked: the plan and every Copilot permission are approved automatically (refusals outside the workspace still apply) and MCP questions are declined. Each automatic answer is listed under **Answered automatically**. The chat gets a link to the task, and when the task finishes the conversation gets a background turn with the result. Incognito chats can't start one.

## Copilot agents

A team can include Copilot agents. They run GitHub Copilot's loop with built-in shell, file and URL tools inside the task's workspace. Every write, command and fetch asks for your approval ("always for this task" is per task). Reads and writes outside the workspace are refused. The operator can switch Copilot agents off with `Fleet:CopilotAgentsEnabled`. After you sign in with GitHub, a Copilot agent can also run on your Copilot plan.

!!! note
    A shell can reach things on the server it runs on, so only run Copilot agents on a deployment you trust.

## Lessons

The **Lessons** button opens what the fleet learned about team design from your finished tasks. Once a night a model reads your recent finished tasks and keeps a few lessons that several tasks agree on. This is **off until you switch it on**, because it spends your own tokens. Each lesson is added to the prompt of every planner in your next tasks, and a task shows "Lessons applied (n)".

The view shows each lesson with its topic, confidence and sources. Lessons expire after 90 days without support. **Disable** keeps a lesson so it isn't proposed again, and delete removes it. **Dream now** runs it on demand. It needs a few finished tasks before it does anything.

The dream also reads your editable prompts (planner, delegation, clarify, judge, retry, follow-up and moderator; never the dream or Copilot prompts) and may propose a small edit when several tasks show one of them steered the teams wrong. **Proposed prompt changes** at the top of the view shows each one with its reason, the tasks behind it and a before/after diff. Nothing changes until you click **Approve**, which applies the edit to your prompt as it is now, so your own later edits stay. Approving saves your own copy of that prompt, so later updates to its default no longer reach it (**Reset** in the prompt editor brings the default back). **Dismiss** changes nothing, and that change isn't proposed again. A proposal whose passage you have since edited away can only be dismissed. Your prompts and short excerpts of each agent's instructions are sent to the dream's model along with the digest.

It also reads your uploaded skills and may propose a small edit to one, or a new skill, when at least three tasks in different workspaces repeat a procedure (never a built-in skill, and a new skill can't take a built-in name). **Proposed skill changes** works like the prompt proposals: nothing happens until you **Approve**. An edit is applied to the skill's text as it is now and leaves its name and description alone; a new skill is installed and turned on (chats and agents with their own tool selection still need a tick). A skill's text can advise using tools, including the shell in a Copilot agent, so read the diff before you approve. Your skill texts are sent to the dream's model too.

## Advanced settings

The **Advanced** button opens the limits the server sets in config, so you can change them for your own tasks and your own dream: team depth, agents per task, judge and approval depth, passing score, attempts, fix rounds, the soft budget, clarifying questions, self-review runs, the model a plain Copilot agent runs on, and the dream's time window, thresholds and lesson caps. Each field shows the server's value as its placeholder; **leave a field blank to use it**. Save checks every value against its allowed range and names the field that is wrong. **Reset all** clears your overrides.

A saved change applies from your **next task**; a task that is already running keeps the settings it started with. The token cap and the other server limits (how many tasks run at once, size limits, the dream's on/off switch) stay the server's and can't be changed here. Raising a limit such as agents per task lets a task do more, within the same token cap. Lowering the number of lessons stored or their lifetime removes nothing at once: it applies at the next dream.

## Editable prompts

The planner, judge, moderator and other prompts can be edited per user. Usage counts under the application "Agent42 fleet" in the usage dashboard.
