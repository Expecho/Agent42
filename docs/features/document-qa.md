# Document Q&A (RAG)

> Add PDFs, Office files or text to a conversation and ask questions about them.

## What it is

Upload a document to a conversation and the agent can answer from it. You don't ask it to "search my
documents": once a conversation has a document, relevant excerpts are pulled in automatically before
each reply, and the agent cites the file and page (or section) it used.

Supported files are PDF, Word (.docx), PowerPoint (.pptx), Excel (.xlsx) and plain text or Markdown.
Documents are for reading only. They are not sent to the model as attachments the way images are.

## Before you start: set an embedding model

Document Q&A needs an **embedding model**, which you choose yourself.

1. Open **Preferences → Model providers**.
2. Add an embedding-capable model to a provider and tick **embeddings** on it.
3. Pick it as your **default embedding model**.

Until you do, the composer won't let you attach documents (images still work). If you remove the model
later, chats with documents keep working but answer without the excerpts. If you switch to a different
embedding model, upload your documents again so they are indexed with the new one.

## Ask about a document

1. Attach the file in the composer and send.
2. Ask your question. This suits targeted questions such as "what does the contract say about notice
   periods?".

Uploading a file with the same name as one already in the conversation replaces it, so you never get
duplicate excerpts.

## Read a whole document

For "summarise all of it" or "list every requirement", turn on the **read_document** tool in
**Preferences → Tools**. The agent then reads the document from start to end, part by part, and takes
notes as it goes. It reads up to 20 parts per turn, so very long files may need a follow-up.

## Documents from the web

When web search is on, the agent can also pull a PDF, Office or text file from a link you typed or a
search surfaced, and add it to the conversation. Web pages (HTML) are not read this way. Limits: 10 MB
per download and 10 web documents per conversation. See [Web search](web-search.md).

## Privacy

- Your documents are only searchable from the conversation they were added to.
- Excerpts are sent to the model provider you picked as part of your message, and the document text is
  sent to your embedding model when it is indexed.
- Deleting the conversation deletes its documents.

## Related

- [Bring your own models](bring-your-own-models.md)
- [Agent fleet](agent-fleet.md) also accepts files for its tasks.
