# Optional skills library

Skills that depend on infrastructure not every repo has. They are NOT active here —
Claude Code only discovers skills under `.claude/skills/`. Onboarding activates one
by **copying its folder** into `.claude/skills/` and filling its `onboard:` markers;
`onboard:` markers in THIS folder are expected and don't count against
onboarding-complete checks.

| Skill | Requires | Activate when |
|-------|----------|---------------|
| `sonar/` | SonarQube server + token | The repo has a Sonar quality gate |
| `jira-implement/` | Atlassian/Jira MCP server | The team works from Jira tickets |
| `spike-dotnet/` | .NET SDK 10+ | The stack is .NET — activate by default |

MCP servers these skills depend on are installed by the user, never by the repo —
onboarding presents the matching install commands from `.agentic/mcp-servers.md`
(`claude mcp add --scope user ...`); no `.mcp.json` or tokens are ever checked in.

Adding to the library: any skill whose first step would be "check whether tool X
exists" belongs here, not in `.claude/skills/`. A default skill must work in every
repo the template is dropped into.
