-- stores: one per consuming application (spec §6.1)
CREATE TABLE IF NOT EXISTS stores (
    id    text PRIMARY KEY,
    name  text NOT NULL DEFAULT ''
);

-- schema_versions: the versioned application schema lineage (spec §6.3)
CREATE TABLE IF NOT EXISTS schema_versions (
    store_id    text    NOT NULL REFERENCES stores(id),
    version     text    NOT NULL,
    definition  jsonb   NOT NULL,
    is_active   boolean NOT NULL DEFAULT false,
    PRIMARY KEY (store_id, version)
);

-- At most one active schema per store.
CREATE UNIQUE INDEX IF NOT EXISTS ux_schema_versions_active
    ON schema_versions (store_id)
    WHERE is_active;

-- tenants: the data-isolation discriminator within a store (spec §6.1)
CREATE TABLE IF NOT EXISTS tenants (
    store_id   text NOT NULL REFERENCES stores(id),
    tenant_id  text NOT NULL,
    PRIMARY KEY (store_id, tenant_id)
);

-- relation_tuples: the atomic stored facts (spec §6.2/§6.3)
CREATE TABLE IF NOT EXISTS relation_tuples (
    store_id           text  NOT NULL,
    tenant_id          text  NOT NULL,
    object_type        text  NOT NULL,
    object_id          text  NOT NULL,
    relation           text  NOT NULL,
    subject_type       text  NOT NULL,
    subject_id         text  NOT NULL,
    subject_relation   text  NULL,
    condition_name     text  NULL,
    condition_params   jsonb NULL,
    FOREIGN KEY (store_id, tenant_id) REFERENCES tenants(store_id, tenant_id)
);

-- Natural key (locked in shared decisions): COALESCE collapses null vs '' relation.
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

-- object_attributes: synced authz-relevant resource fields (spec §6.4)
CREATE TABLE IF NOT EXISTS object_attributes (
    store_id     text  NOT NULL,
    tenant_id    text  NOT NULL,
    object_type  text  NOT NULL,
    object_id    text  NOT NULL,
    attributes   jsonb NOT NULL DEFAULT '{}'::jsonb,
    PRIMARY KEY (store_id, tenant_id, object_type, object_id),
    FOREIGN KEY (store_id, tenant_id) REFERENCES tenants(store_id, tenant_id)
);

-- reverse_index: the maintained structural expansion (M2; spec §6.3/§7.3)
CREATE TABLE IF NOT EXISTS reverse_index (
    store_id        text    NOT NULL,
    tenant_id       text    NOT NULL,
    schema_version  text    NOT NULL,
    subject         text    NOT NULL,
    permission      text    NOT NULL,
    object_type     text    NOT NULL,
    object_id       text    NOT NULL,
    conditioned     boolean NOT NULL DEFAULT false,
    FOREIGN KEY (store_id, tenant_id) REFERENCES tenants(store_id, tenant_id)
);

-- Scan index for ListObjects: (store, tenant, schema_version, subject, permission, object_type).
CREATE INDEX IF NOT EXISTS ix_reverse_index_scan
    ON reverse_index (store_id, tenant_id, schema_version, subject, permission, object_type);

-- cache_entries: UNLOGGED Postgres-native cache (M2; spec §9.1)
CREATE UNLOGGED TABLE IF NOT EXISTS cache_entries (
    store_id    text        NOT NULL,
    tenant_id   text        NOT NULL,
    key         text        NOT NULL,
    value       bytea       NOT NULL,
    epoch       bigint      NOT NULL,
    expires_at  timestamptz NOT NULL,
    PRIMARY KEY (store_id, tenant_id, key)
);

CREATE INDEX IF NOT EXISTS ix_cache_entries_expiry
    ON cache_entries (expires_at);

-- change_log: append-only config-change audit (spec §6.5)
CREATE TABLE IF NOT EXISTS change_log (
    id           bigserial   PRIMARY KEY,
    store_id     text        NOT NULL,
    tenant_id    text        NOT NULL,
    actor        text        NOT NULL,
    operation    text        NOT NULL,
    target       text        NOT NULL,
    before       jsonb       NULL,
    after        jsonb       NULL,
    occurred_at  timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (store_id, tenant_id) REFERENCES tenants(store_id, tenant_id)
);

CREATE INDEX IF NOT EXISTS ix_change_log_read
    ON change_log (store_id, tenant_id, occurred_at DESC);

-- tenant_epochs: authoritative cache-epoch counter (CONTRACT GAP)
-- Backs ICacheStore.GetEpochAsync / BumpEpochAsync; not a spec §6.3 table.
CREATE TABLE IF NOT EXISTS tenant_epochs (
    store_id   text   NOT NULL,
    tenant_id  text   NOT NULL,
    epoch      bigint NOT NULL DEFAULT 0,
    PRIMARY KEY (store_id, tenant_id),
    FOREIGN KEY (store_id, tenant_id) REFERENCES tenants(store_id, tenant_id)
);
