CREATE TABLE custodex.stores (
    id    nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL CONSTRAINT pk_stores PRIMARY KEY,
    name  nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL CONSTRAINT df_stores_name DEFAULT N''
);

CREATE TABLE custodex.schema_versions (
    store_id    nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    version     nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    definition  nvarchar(max) NOT NULL,
    is_active   bit NOT NULL CONSTRAINT df_schema_versions_active DEFAULT 0,
    CONSTRAINT pk_schema_versions PRIMARY KEY (store_id, version),
    CONSTRAINT fk_schema_versions_store FOREIGN KEY (store_id) REFERENCES custodex.stores (id)
);

CREATE UNIQUE INDEX ux_schema_versions_active
    ON custodex.schema_versions (store_id)
    WHERE is_active = 1;

CREATE TABLE custodex.tenants (
    store_id   nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    tenant_id  nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    CONSTRAINT pk_tenants PRIMARY KEY (store_id, tenant_id),
    CONSTRAINT fk_tenants_store FOREIGN KEY (store_id) REFERENCES custodex.stores (id)
);

CREATE TABLE custodex.relation_tuples (
    store_id           nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    tenant_id          nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    object_type        nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    object_id          nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    relation           nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    subject_type       nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    subject_id         nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    subject_relation   nvarchar(128) COLLATE Latin1_General_100_BIN2 NULL,
    condition_name     nvarchar(128) COLLATE Latin1_General_100_BIN2 NULL,
    condition_params   nvarchar(max) NULL,
    natural_key AS CONVERT(binary(32), HASHBYTES('SHA2_256', CONCAT_WS(NCHAR(31),
        store_id, tenant_id, object_type, object_id, relation,
        subject_type, subject_id, ISNULL(subject_relation, N'')))) PERSISTED NOT NULL,
    CONSTRAINT fk_relation_tuples_tenant FOREIGN KEY (store_id, tenant_id)
        REFERENCES custodex.tenants (store_id, tenant_id)
);

CREATE UNIQUE INDEX ux_relation_tuples_natural
    ON custodex.relation_tuples (natural_key);

CREATE INDEX ix_relation_tuples_forward
    ON custodex.relation_tuples (store_id, tenant_id, object_type, object_id, relation);

CREATE INDEX ix_relation_tuples_reverse
    ON custodex.relation_tuples (store_id, tenant_id, subject_type, subject_id);

CREATE TABLE custodex.object_attributes (
    store_id     nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    tenant_id    nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    object_type  nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    object_id    nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    attributes   nvarchar(max) NOT NULL CONSTRAINT df_object_attributes DEFAULT N'{}',
    CONSTRAINT pk_object_attributes PRIMARY KEY (store_id, tenant_id, object_type, object_id),
    CONSTRAINT fk_object_attributes_tenant FOREIGN KEY (store_id, tenant_id)
        REFERENCES custodex.tenants (store_id, tenant_id)
);

CREATE TABLE custodex.cache_entries (
    store_id    nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    tenant_id   nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    cache_key   nvarchar(400) COLLATE Latin1_General_100_BIN2 NOT NULL,
    value       varbinary(max) NOT NULL,
    epoch       bigint NOT NULL,
    expires_at  datetime2 NOT NULL,
    CONSTRAINT pk_cache_entries PRIMARY KEY (store_id, tenant_id, cache_key)
);

CREATE INDEX ix_cache_entries_expiry
    ON custodex.cache_entries (expires_at);

CREATE TABLE custodex.change_log (
    id           bigint IDENTITY(1, 1) NOT NULL CONSTRAINT pk_change_log PRIMARY KEY,
    store_id     nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    tenant_id    nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    actor        nvarchar(256) COLLATE Latin1_General_100_BIN2 NOT NULL,
    operation    nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    target       nvarchar(900) COLLATE Latin1_General_100_BIN2 NOT NULL,
    before       nvarchar(max) NULL,
    after        nvarchar(max) NULL,
    occurred_at  datetime2 NOT NULL CONSTRAINT df_change_log_occurred DEFAULT SYSUTCDATETIME(),
    CONSTRAINT fk_change_log_tenant FOREIGN KEY (store_id, tenant_id)
        REFERENCES custodex.tenants (store_id, tenant_id)
);

CREATE INDEX ix_change_log_read
    ON custodex.change_log (store_id, tenant_id, occurred_at DESC);

CREATE TABLE custodex.tenant_epochs (
    store_id   nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    tenant_id  nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    epoch      bigint NOT NULL CONSTRAINT df_tenant_epochs DEFAULT 0,
    CONSTRAINT pk_tenant_epochs PRIMARY KEY (store_id, tenant_id),
    CONSTRAINT fk_tenant_epochs_tenant FOREIGN KEY (store_id, tenant_id)
        REFERENCES custodex.tenants (store_id, tenant_id)
);
