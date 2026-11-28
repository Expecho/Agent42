# Web search

The assistant can search the web for current facts, prices and recent events. It answers from the short list of titles, links and snippets that come back.

## Turn it on

**Web search** is a tool you can switch on or off. See [Tool selection](tool-selection.md). Results come from a search service built into Agent 42, so you don't need an API key.

An operator can add hosted search providers in front of it: **Tavily** and **Firecrawl**, each with its own key (`WebSearch:Tavily:ApiKey`, `WebSearch:Firecrawl:ApiKey`; in Azure the `TAVILY_API_KEY` and `FIRECRAWL_API_KEY` secrets; in Docker the same names in `.env`). They are tried in the order of `WebSearch:Providers` (default `tavily,firecrawl`), then the built-in service. A provider that fails (out of credits, rate limited, bad key, timeout) is skipped for a while, and the next one answers, so search keeps working. A provider that answers with no results is not treated as a failure. **Setting a key sends your search queries to that vendor.** Preferences → Configuration shows which keys are set and which providers are currently skipped.

On models that use the Responses API, the model provider's own web search is used instead. You use it the same way.

## Use it

Ask something that needs fresh information, such as "what's the weather forecast for Friday" or "latest release of X". The assistant decides when to search and shows the tool call in the chat.

## Limits

- It returns a handful of results (up to five), as snippets only.
- It does **not** open ordinary web pages. If a snippet isn't enough, the assistant says so and asks you to paste the text.
- Documents are the exception. With **Read web documents** switched on, the assistant can read a PDF, Office or text file, but only from a link that came back in a search result or that you pasted yourself. See [Document Q&A](document-qa.md).

## When results look empty

Search engines sometimes block automated queries, which looks like "no results". Open **Preferences → Configuration** and follow the link to the search view (`/searxng/`) to see which engines answered and which failed. Settings you change there only affect that view, not the assistant. Offline development mode has no search service, so the view is unavailable there.
