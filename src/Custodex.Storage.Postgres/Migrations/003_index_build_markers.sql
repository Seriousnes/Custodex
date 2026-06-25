CREATE TABLE IF NOT EXISTS index_build_markers (
    store_id        text        COLLATE "C" NOT NULL,
    tenant_id       text        COLLATE "C" NOT NULL,
    schema_version  text        COLLATE "C" NOT NULL,
    built_at        timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (store_id, tenant_id, schema_version),
    FOREIGN KEY (store_id, tenant_id) REFERENCES tenants(store_id, tenant_id)
);
