# Usage & diagnostics

> See how many tokens you've used and what they cost, plus pointers to the operator tools.

## Token usage and estimated cost

Open the usage view in the app to see your token and tool-call counts per model and per agent, over the
last 24 hours, 7 days, 30 days or all time. You only ever see your own usage.

### Estimated cost

The per-reply token line and the usage view show an **estimated** dollar cost, worked out from token
counts and a built-in price table. It's an estimate, not a bill.

| Provider | Shown as |
|----------|----------|
| Azure OpenAI, Anthropic | `≈ $0.0123`, based on list prices |
| GitHub Copilot | `included` (covered by your subscription) |
| OpenAI-compatible endpoints (GitHub Models, Ollama, vLLM), or a model with no price | `—`, never `$0` |

Tips:

- A model with no known price shows `—` until a price is added.
- Usage recorded before cost estimates existed stays unpriced.
- Anthropic cache writes are priced at the normal input rate in the usage view, so it may read slightly
  low there.

## For operators

These views are for the person running the instance and are visible to **owners** only (the object ids in
the `Access:Owners` setting). Everyone else gets a "not found". It is a convenience gate, not a hard
security boundary.

- **Live console** at `/logs`: recent traces, logs and metrics from the running app. It can include
  prompts and replies, so treat it as sensitive. In production the log level is Warning, so the Logs tab
  is often empty.
- **Dashboard** at `/dashboard`: an overview of the app's internal state and its health.
- **Application Insights**: set the `APPLICATIONINSIGHTS_CONNECTION_STRING` secret to also send traces and
  metrics to Azure Monitor. Conversation content is removed before it is sent.

See also [Configuration health](configuration-health.md).
