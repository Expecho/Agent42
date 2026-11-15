# Rich inline rendering

Diagrams, highlighted code, math and charts render inside an assistant reply, both in the chat and in shared read-only links.

## What renders

When a reply is finished, these blocks turn into rendered content:

| In the reply | Renders as |
|---|---|
| a `mermaid` code block | a diagram |
| any other code block | syntax highlighting, plus a copy button that copies the raw code |
| `$...$` or `\(...\)` | inline math |
| `$$...$$` or `\[...\]` | display math |
| a `chart` code block with a Chart.js config | a chart |

While a reply streams, these blocks show as plain code and are rendered once the reply ends.

## When something doesn't render

If a block is broken (a diagram syntax error, invalid chart data), its code stays visible with a short red note under it. The rest of the reply still renders.

Single dollar signs are treated as math only when it is clearly math. "It costs $5 and $10" stays plain text.

## Asking for them

The model already knows the chart format, so you can just ask: "chart these numbers", "draw a flowchart of this process", or "show the formula".

## Privacy and loading

Everything is rendered in your browser, with the libraries served from Agent42 itself. Nothing is fetched from a third-party CDN. Larger libraries, such as the diagram renderer, load the first time a reply needs them.

## Try it offline

Run the offline dev host (`dotnet run --project tools/Agent42.DevHost`) and send **show rich**. The canned reply contains one of each block, plus a broken diagram and a currency sentence.
