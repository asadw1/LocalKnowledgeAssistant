-- Metadata index over the allow-listed document root. Written only by the indexer; the agent gets read-only access.

CREATE TABLE files (
    id           INTEGER PRIMARY KEY,
    path         TEXT    NOT NULL UNIQUE,  -- absolute path, exactly as the filesystem tools expect it
    name         TEXT    NOT NULL,
    extension    TEXT    NOT NULL,         -- lower-case, with leading dot, or empty
    size_bytes   INTEGER NOT NULL,
    modified_utc TEXT    NOT NULL,         -- ISO 8601, e.g. 2026-10-08T21:14:31.2846206Z
    hash         TEXT    NOT NULL,         -- SHA-256, lower-case hex
    indexed_utc  TEXT    NOT NULL
);

CREATE INDEX ix_files_modified ON files (modified_utc);
CREATE INDEX ix_files_extension ON files (extension);

CREATE TABLE tags (
    id   INTEGER PRIMARY KEY,
    name TEXT NOT NULL UNIQUE COLLATE NOCASE
);

CREATE TABLE file_tags (
    file_id INTEGER NOT NULL REFERENCES files (id) ON DELETE CASCADE,
    tag_id  INTEGER NOT NULL REFERENCES tags (id) ON DELETE CASCADE,
    PRIMARY KEY (file_id, tag_id)
);

CREATE TABLE summaries (
    file_id       INTEGER PRIMARY KEY REFERENCES files (id) ON DELETE CASCADE,
    summary       TEXT NOT NULL,
    source_hash   TEXT NOT NULL,           -- file hash the summary was written for; stale when it differs from files.hash
    generated_utc TEXT NOT NULL
);
