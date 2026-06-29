CREATE TABLE custodex.reverse_index (
    store_id        nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    tenant_id       nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    schema_version  nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    subject         nvarchar(400) COLLATE Latin1_General_100_BIN2 NOT NULL,
    permission      nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    object_type     nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    object_id       nvarchar(128) COLLATE Latin1_General_100_BIN2 NOT NULL,
    conditioned     bit NOT NULL CONSTRAINT df_reverse_index_conditioned DEFAULT 0,
    natural_key AS CONVERT(binary(32), HASHBYTES('SHA2_256', CONCAT_WS(NCHAR(31),
        store_id, tenant_id, schema_version, subject, permission, object_type, object_id))) PERSISTED NOT NULL,
    scan_key AS CONVERT(binary(32), HASHBYTES('SHA2_256', CONCAT_WS(NCHAR(31),
        store_id, tenant_id, schema_version, subject, permission, object_type))) PERSISTED NOT NULL,
    CONSTRAINT fk_reverse_index_tenant FOREIGN KEY (store_id, tenant_id)
        REFERENCES custodex.tenants (store_id, tenant_id)
);

CREATE UNIQUE INDEX ux_reverse_index_natural
    ON custodex.reverse_index (natural_key);

CREATE INDEX ix_reverse_index_scan
    ON custodex.reverse_index (scan_key, object_id);
