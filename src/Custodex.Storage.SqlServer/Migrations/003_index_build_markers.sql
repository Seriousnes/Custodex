CREATE TABLE custodex.index_build_markers (
    store_id        nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    tenant_id       nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    schema_version  nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    built_at        datetime2 NOT NULL CONSTRAINT df_index_build_markers_built DEFAULT SYSUTCDATETIME(),
    CONSTRAINT pk_index_build_markers PRIMARY KEY (store_id, tenant_id, schema_version),
    CONSTRAINT fk_index_build_markers_tenant FOREIGN KEY (store_id, tenant_id)
        REFERENCES custodex.tenants (store_id, tenant_id)
);
