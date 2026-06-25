-- All Custodex objects live in a dedicated schema, isolated from the consumer's own tables.
CREATE SCHEMA IF NOT EXISTS custodex;

-- Identifier text columns use COLLATE "C" so equality and ordering are byte-exact (ordinal),
-- matching the engine's ordinal comparison regardless of the database's default collation.

-- stores: one per consuming application
CREATE TABLE IF NOT EXISTS stores (
    id    text COLLATE "C" PRIMARY KEY,
    name  text COLLATE "C" NOT NULL DEFAULT ''
);

-- schema_versions: the versioned application schema lineage
CREATE TABLE IF NOT EXISTS schema_versions (
    store_id    text  COLLATE "C" NOT NULL REFERENCES stores(id),
    version     text  COLLATE "C" NOT NULL,
    definition  jsonb NOT NULL,
    is_active   boolean NOT NULL DEFAULT false,
    PRIMARY KEY (store_id, version)
);

-- At most one active schema per store.
CREATE UNIQUE INDEX IF NOT EXISTS ux_schema_versions_active
    ON schema_versions (store_id)
    WHERE is_active;

-- tenants: the data-isolation discriminator within a store
CREATE TABLE IF NOT EXISTS tenants (
    store_id   text COLLATE "C" NOT NULL REFERENCES stores(id),
    tenant_id  text COLLATE "C" NOT NULL,
    PRIMARY KEY (store_id, tenant_id)
);

-- relation_tuples: the atomic stored facts
CREATE TABLE IF NOT EXISTS relation_tuples (
    store_id           text  COLLATE "C" NOT NULL,
    tenant_id          text  COLLATE "C" NOT NULL,
    object_type        text  COLLATE "C" NOT NULL,
    object_id          text  COLLATE "C" NOT NULL,
    relation           text  COLLATE "C" NOT NULL,
    subject_type       text  COLLATE "C" NOT NULL,
    subject_id         text  COLLATE "C" NOT NULL,
    subject_relation   text  COLLATE "C" NULL,
    condition_name     text  COLLATE "C" NULL,
    condition_params   jsonb NULL,
    FOREIGN KEY (store_id, tenant_id) REFERENCES tenants(store_id, tenant_id)
);

-- Natural key: COALESCE collapses null vs '' relation.
CREATE UNIQUE INDEX IF NOT EXISTS ux_relation_tuples_natural
    ON relation_tuples (
        store_id, tenant_id, object_type, object_id, relation,
        subject_type, subject_id, COALESCE(subject_relation, '')
    );

-- Forward index for Check: (store, tenant, object_type, object_id, relation).
CREATE INDEX IF NOT EXISTS ix_relation_tuples_forward
    ON relation_tuples (store_id, tenant_id, object_type, object_id, relation);

-- Reverse index for traversal / ListSubjects: (store, tenant, subject_type, subject_id).
CREATE INDEX IF NOT EXISTS ix_relation_tuples_reverse
    ON relation_tuples (store_id, tenant_id, subject_type, subject_id);

-- object_attributes: synced authz-relevant resource fields
CREATE TABLE IF NOT EXISTS object_attributes (
    store_id     text  COLLATE "C" NOT NULL,
    tenant_id    text  COLLATE "C" NOT NULL,
    object_type  text  COLLATE "C" NOT NULL,
    object_id    text  COLLATE "C" NOT NULL,
    attributes   jsonb NOT NULL DEFAULT '{}'::jsonb,
    PRIMARY KEY (store_id, tenant_id, object_type, object_id),
    FOREIGN KEY (store_id, tenant_id) REFERENCES tenants(store_id, tenant_id)
);

-- reverse_index: the maintained structural expansion
CREATE TABLE IF NOT EXISTS reverse_index (
    store_id        text    COLLATE "C" NOT NULL,
    tenant_id       text    COLLATE "C" NOT NULL,
    schema_version  text    COLLATE "C" NOT NULL,
    subject         text    COLLATE "C" NOT NULL,
    permission      text    COLLATE "C" NOT NULL,
    object_type     text    COLLATE "C" NOT NULL,
    object_id       text    COLLATE "C" NOT NULL,
    conditioned     boolean NOT NULL DEFAULT false,
    FOREIGN KEY (store_id, tenant_id) REFERENCES tenants(store_id, tenant_id)
);

-- Scan index for ListObjects: (store, tenant, schema_version, subject, permission, object_type).
CREATE INDEX IF NOT EXISTS ix_reverse_index_scan
    ON reverse_index (store_id, tenant_id, schema_version, subject, permission, object_type);

-- cache_entries: UNLOGGED Postgres-native cache
CREATE UNLOGGED TABLE IF NOT EXISTS cache_entries (
    store_id    text        COLLATE "C" NOT NULL,
    tenant_id   text        COLLATE "C" NOT NULL,
    key         text        COLLATE "C" NOT NULL,
    value       bytea       NOT NULL,
    epoch       bigint      NOT NULL,
    expires_at  timestamptz NOT NULL,
    PRIMARY KEY (store_id, tenant_id, key)
);

CREATE INDEX IF NOT EXISTS ix_cache_entries_expiry
    ON cache_entries (expires_at);

-- change_log: append-only config-change audit
CREATE TABLE IF NOT EXISTS change_log (
    id           bigserial   PRIMARY KEY,
    store_id     text        COLLATE "C" NOT NULL,
    tenant_id    text        COLLATE "C" NOT NULL,
    actor        text        COLLATE "C" NOT NULL,
    operation    text        COLLATE "C" NOT NULL,
    target       text        COLLATE "C" NOT NULL,
    before       jsonb       NULL,
    after        jsonb       NULL,
    occurred_at  timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (store_id, tenant_id) REFERENCES tenants(store_id, tenant_id)
);

CREATE INDEX IF NOT EXISTS ix_change_log_read
    ON change_log (store_id, tenant_id, occurred_at DESC);

-- tenant_epochs: authoritative cache-epoch counter backing ICacheStore.GetEpochAsync / BumpEpochAsync.
CREATE TABLE IF NOT EXISTS tenant_epochs (
    store_id   text   COLLATE "C" NOT NULL,
    tenant_id  text   COLLATE "C" NOT NULL,
    epoch      bigint NOT NULL DEFAULT 0,
    PRIMARY KEY (store_id, tenant_id),
    FOREIGN KEY (store_id, tenant_id) REFERENCES tenants(store_id, tenant_id)
);
