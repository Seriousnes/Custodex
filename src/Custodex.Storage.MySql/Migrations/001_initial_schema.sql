CREATE TABLE IF NOT EXISTS stores (
    id    varchar(64)  CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    name  varchar(255) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL DEFAULT '',
    PRIMARY KEY (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS schema_versions (
    store_id      varchar(64)  CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    version       varchar(255) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    definition    longtext NOT NULL,
    is_active     tinyint(1) NOT NULL DEFAULT 0,
    active_store  varchar(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin
                  AS (IF(is_active = 1, store_id, NULL)) STORED,
    PRIMARY KEY (store_id, version),
    UNIQUE KEY ux_schema_versions_active (active_store),
    CONSTRAINT fk_schema_versions_store FOREIGN KEY (store_id) REFERENCES stores (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS tenants (
    store_id   varchar(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    tenant_id  varchar(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    PRIMARY KEY (store_id, tenant_id),
    CONSTRAINT fk_tenants_store FOREIGN KEY (store_id) REFERENCES stores (id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS relation_tuples (
    store_id          varchar(64)  CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    tenant_id         varchar(64)  CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    object_type       varchar(64)  CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    object_id         varchar(180) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    relation          varchar(64)  CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    subject_type      varchar(64)  CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    subject_id        varchar(180) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    subject_relation  varchar(64)  CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL DEFAULT '',
    condition_name    varchar(255) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL,
    condition_params  longtext NULL,
    UNIQUE KEY ux_relation_tuples_natural (
        store_id, tenant_id, object_type, object_id, relation,
        subject_type, subject_id, subject_relation
    ),
    KEY ix_relation_tuples_forward (store_id, tenant_id, object_type, object_id, relation),
    KEY ix_relation_tuples_reverse (store_id, tenant_id, subject_type, subject_id),
    CONSTRAINT fk_relation_tuples_tenant
        FOREIGN KEY (store_id, tenant_id) REFERENCES tenants (store_id, tenant_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS object_attributes (
    store_id     varchar(64)  CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    tenant_id    varchar(64)  CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    object_type  varchar(64)  CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    object_id    varchar(180) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    attributes   longtext NOT NULL,
    PRIMARY KEY (store_id, tenant_id, object_type, object_id),
    CONSTRAINT fk_object_attributes_tenant
        FOREIGN KEY (store_id, tenant_id) REFERENCES tenants (store_id, tenant_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS change_log (
    id           bigint NOT NULL AUTO_INCREMENT,
    store_id     varchar(64)  CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    tenant_id    varchar(64)  CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    actor        varchar(255) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    operation    varchar(64)  CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    target       text CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    `before`     longtext NULL,
    `after`      longtext NULL,
    occurred_at  datetime(6) NOT NULL DEFAULT CURRENT_TIMESTAMP(6),
    PRIMARY KEY (id),
    KEY ix_change_log_read (store_id, tenant_id, occurred_at),
    CONSTRAINT fk_change_log_tenant
        FOREIGN KEY (store_id, tenant_id) REFERENCES tenants (store_id, tenant_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS tenant_epochs (
    store_id   varchar(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    tenant_id  varchar(64) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    epoch      bigint NOT NULL DEFAULT 0,
    PRIMARY KEY (store_id, tenant_id),
    CONSTRAINT fk_tenant_epochs_tenant
        FOREIGN KEY (store_id, tenant_id) REFERENCES tenants (store_id, tenant_id)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;

CREATE TABLE IF NOT EXISTS cache_entries (
    store_id    varchar(64)  CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    tenant_id   varchar(64)  CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    `key`       varchar(255) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
    value       longblob NOT NULL,
    epoch       bigint NOT NULL,
    expires_at  datetime(6) NOT NULL,
    PRIMARY KEY (store_id, tenant_id, `key`),
    KEY ix_cache_entries_expiry (expires_at)
) ENGINE=InnoDB DEFAULT CHARSET=utf8mb4;
