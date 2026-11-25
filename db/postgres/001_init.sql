-- FlowMind vector store bootstrap. Optional: the application creates this itself (idempotent) on first use.
-- Run manually only if the app's database user is not allowed to CREATE EXTENSION / CREATE TABLE.
-- vector(1024) must match AiProviders:Cloudflare:Dimensions (BGE-M3 = 1024; verify with a live call).
CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE IF NOT EXISTS document_chunks (
    id                  uuid        PRIMARY KEY,
    tenant_id           uuid        NOT NULL,
    application_id      uuid        NOT NULL,
    document_id         uuid        NOT NULL,
    document_version_id uuid        NOT NULL,
    document_name       text        NOT NULL,
    chunk_index         integer     NOT NULL,
    page_number         integer     NOT NULL,
    content             text        NOT NULL,
    content_hash        char(64)    NOT NULL,
    embedding           vector(1024) NOT NULL,
    tsv                 tsvector GENERATED ALWAYS AS (to_tsvector('english', content)) STORED,
    created_utc         timestamptz NOT NULL DEFAULT now(),
    UNIQUE (document_version_id, chunk_index)
);
CREATE INDEX IF NOT EXISTS ix_chunks_scope ON document_chunks (tenant_id, application_id);
CREATE INDEX IF NOT EXISTS ix_chunks_doc   ON document_chunks (tenant_id, document_id);
CREATE INDEX IF NOT EXISTS ix_chunks_emb   ON document_chunks USING hnsw (embedding vector_cosine_ops);
CREATE INDEX IF NOT EXISTS ix_chunks_tsv   ON document_chunks USING gin (tsv);
