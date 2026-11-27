// Minimal OpenAI-compatible chat-completions stub for the self-host smoke (ADR 0151). Self-host has no operator
// model (ADR 0019/0030), so the test registers this as a per-user provider. Streams a fixed reply as SSE.
import http from "node:http";

const REPLY = process.env.STUB_REPLY ?? "Stub reply from the e2e LLM: forty-two.";
const port = Number(process.env.PORT ?? 8099);

const sse = (res, obj) => res.write(`data: ${JSON.stringify(obj)}\n\n`);
const chunk = (delta, finish = null) => ({
  id: "chatcmpl-stub", object: "chat.completion.chunk", created: 0, model: "stub-model",
  choices: [{ index: 0, delta, finish_reason: finish }],
});

http.createServer((req, res) => {
  let body = "";
  req.on("data", (d) => { body += d; });
  req.on("end", () => {
    if (req.method === "GET") { res.writeHead(200).end("ok"); return; }
    if (!req.url.endsWith("/chat/completions")) { res.writeHead(404).end(); return; }
    const wantsStream = JSON.parse(body || "{}").stream === true;
    if (!wantsStream) {
      res.writeHead(200, { "content-type": "application/json" }).end(JSON.stringify({
        id: "chatcmpl-stub", object: "chat.completion", created: 0, model: "stub-model",
        choices: [{ index: 0, message: { role: "assistant", content: REPLY }, finish_reason: "stop" }],
        usage: { prompt_tokens: 5, completion_tokens: 7, total_tokens: 12 },
      }));
      return;
    }
    res.writeHead(200, { "content-type": "text/event-stream", "cache-control": "no-cache" });
    sse(res, chunk({ role: "assistant", content: "" }));
    for (const word of REPLY.split(/(?<= )/)) sse(res, chunk({ content: word }));
    sse(res, chunk({}, "stop"));
    res.write("data: [DONE]\n\n");
    res.end();
  });
}).listen(port, "0.0.0.0", () => console.log(`llm-stub listening on ${port}`));
