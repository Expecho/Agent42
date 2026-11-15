# Skills

A skill is a reusable set of written instructions (a playbook) that the assistant loads only when a request matches it. Two skills ship with Agent 42, and you can upload your own.

## What skills do

Each skill has a name and a short description. The assistant always sees those, and when your request matches one it loads the full instructions and follows them. This keeps chats light: a skill costs nothing until it is used.

## Built-in skills

- **websearch**: guidance for searching the web. It is on exactly when **Web search** is on.
- **scheduledprompt**: guidance for reminders. It is on exactly when **Scheduling** is on.

They don't appear as separate rows in the tool list.

## Upload your own

1. Open **Preferences → Skills**.
2. Click **Upload skill**.
3. Choose a `SKILL.md` file, or a `.zip` containing one or more skill folders with any reference files they need.
4. Open the **Tools** tab and make sure your skill is switched on.

A `SKILL.md` starts with a header giving the skill's `name` and `description`, followed by the instructions in markdown.

Changes take effect on your next new chat. Existing chats and agents keep the tools they were given. Uploading a skill with the same name again replaces the old one, and a skill named after a built-in one replaces that built-in for you.

## Limits and safety

- Scripts are never stored or run. Only the instructions and reference files are kept.
- Long skill text and long reference files are cut short when the assistant reads them.
- A skill's text becomes instructions the model follows, so upload only skills you trust.

## Turn skills on or off

Each uploaded skill has its own row under **Preferences → Tools**, in the composer's tools popover and in the agent editor. See [Tool selection](tool-selection.md). Models that can't use tools get no skills.
