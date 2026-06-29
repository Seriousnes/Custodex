CREATE TABLE IF NOT EXISTS stores (
    id    TEXT PRIMARY KEY,
    name  TEXT NOT NULL DEFAULT ''
);

CREATE TABLE IF NOT EXISTS tenants (
    store_id   TEXT NOT NULL,
    tenant_id  TEXT NOT NULL,
    PRIMARY KEY (store_id, tenant_id),
    FOREIGN KEY (store_id) REFERENCES stores(id)
);

CREATE TABLE IF NOT EXISTS schema_versions (
    store_id    TEXT NOT NULL,
    version     TEXT NOT NULL,
    definition  TEXT NOT NULL,
    is_active   INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (store_id, version),
    FOREIGN KEY (store_id) REFERENCES stores(id)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_schema_versions_active
    ON schema_versions (store_id)
    WHERE is_active = 1;

CREATE TABLE IF NOT EXISTS relation_tuples (
    store_id           TEXT NOT NULL,
    tenant_id          TEXT NOT NULL,
    object_type        TEXT NOT NULL,
    object_id          TEXT NOT NULL,
    relation           TEXT NOT NULL,
    subject_type       TEXT NOT NULL,
    subject_id         TEXT NOT NULL,
    subject_relation   TEXT NULL,
    condition_name     TEXT NULL,
    condition_params   TEXT NULL,
    FOREIGN KEY (store_id, tenant_id) REFERENCES tenants(store_id, tenant_id)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_relation_tuples_natural
    ON relation_tuples (
        store_id, tenant_id, object_type, object_id, relation,
        subject_type, subject_id, COALESCE(subject_relation, '')
    );

CREATE INDEX IF NOT EXISTS ix_relation_tuples_forward
    ON relation_tuples (store_id, tenant_id, object_type, object_id, relation);

CREATE INDEX IF NOT EXISTS ix_relation_tuples_reverse
    ON relation_tuples (store_id, tenant_id, subject_type, subject_id);

CREATE TABLE IF NOT EXISTS object_attributes (
    store_id     TEXT NOT NULL,
    tenant_id    TEXT NOT NULL,
    object_type  TEXT NOT NULL,
    object_id    TEXT NOT NULL,
    attributes   TEXT NOT NULL DEFAULT '{}',
    PRIMARY KEY (store_id, tenant_id, object_type, object_id),
    FOREIGN KEY (store_id, tenant_id) REFERENCES tenants(store_id, tenant_id)
);

CREATE TABLE IF NOT EXISTS change_log (
    id           INTEGER PRIMARY KEY,
    store_id     TEXT NOT NULL,
    tenant_id    TEXT NOT NULL,
    actor        TEXT NOT NULL,
    operation    TEXT NOT NULL,
    target       TEXT NOT NULL,
    before       TEXT NULL,
    after        TEXT NULL,
    occurred_at  TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    FOREIGN KEY (store_id, tenant_id) REFERENCES tenants(store_id, tenant_id)
);

CREATE INDEX IF NOT EXISTS ix_change_log_read
    ON change_log (store_id, tenant_id, occurred_at DESC);

CREATE TABLE IF NOT EXISTS tenant_epochs (
    store_id   TEXT NOT NULL,
    tenant_id  TEXT NOT NULL,
    epoch      INTEGER NOT NULL DEFAULT 0,
    PRIMARY KEY (store_id, tenant_id),
    FOREIGN KEY (store_id, tenant_id) REFERENCES tenants(store_id, tenant_id)
);

CREATE TABLE IF NOT EXISTS cache_entries (
    store_id    TEXT    NOT NULL,
    tenant_id   TEXT    NOT NULL,
    key         TEXT    NOT NULL,
    value       BLOB    NOT NULL,
    epoch       INTEGER NOT NULL,
    expires_at  INTEGER NOT NULL,
    PRIMARY KEY (store_id, tenant_id, key),
    FOREIGN KEY (store_id, tenant_id) REFERENCES tenants(store_id, tenant_id)
);

CREATE INDEX IF NOT EXISTS ix_cache_entries_expiry
    ON cache_entries (expires_at);
