-- Agent42's own tables for Storage:Provider = local. Re-run on every start, so every statement is IF NOT EXISTS.
-- {DIMENSIONS} is replaced with Storage:Local:DocChunksDimensions (default 3072) before the script runs.

CREATE EXTENSION IF NOT EXISTS vector;

-- The document index (ADR 0007): one row per chunk. No ANN index: HNSW/IVFFlat cannot index a vector above 2000
-- dimensions and the default is 3072, so search is an exact scan scoped to one owner (ADR 0127).
CREATE TABLE IF NOT EXISTS agent42_doc_chunks
(
    seq         bigserial PRIMARY KEY,
    owner_key   text    NOT NULL,
    file_name   text    NOT NULL,
    page        integer NULL,
    chunk_index integer NOT NULL,
    text        text    NOT NULL,
    embedding   vector({DIMENSIONS}) NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_agent42_doc_chunks_owner ON agent42_doc_chunks (owner_key, file_name, chunk_index);

-- The chat search index (ADR 0061): one row per turn, keyed like the Cosmos documents (conversation + ordinal).
CREATE TABLE IF NOT EXISTS agent42_chat_messages
(
    user_id         text    NOT NULL,
    conversation_id text    NOT NULL,
    ordinal         integer NOT NULL,
    role            text    NOT NULL,
    text            text    NOT NULL,
    PRIMARY KEY (user_id, conversation_id, ordinal)
);
CREATE INDEX IF NOT EXISTS ix_agent42_chat_messages_fts ON agent42_chat_messages USING gin (to_tsvector('simple', text));
