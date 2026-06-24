CREATE UNIQUE INDEX IF NOT EXISTS ux_reverse_index_natural
    ON reverse_index (
        store_id, tenant_id, schema_version, subject, permission, object_type, object_id
    );
