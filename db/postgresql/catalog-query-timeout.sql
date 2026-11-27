\set ON_ERROR_STOP on
-- Preserve audit history. The caller owns the transaction.
-- Install the nullable audit cell before any backfill can fire a refreshed trigger.
ALTER TABLE datagateway.datasourceaudit ADD COLUMN IF NOT EXISTS query_timeout_seconds integer;
ALTER TABLE datagateway.datasource ADD COLUMN IF NOT EXISTS query_timeout_seconds integer DEFAULT 30;
UPDATE datagateway.datasource SET query_timeout_seconds = 30 WHERE query_timeout_seconds IS NULL;
ALTER TABLE datagateway.datasource ALTER COLUMN query_timeout_seconds SET DEFAULT 30;
ALTER TABLE datagateway.datasource ALTER COLUMN query_timeout_seconds SET NOT NULL;
DO $migration$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_constraint
        WHERE conrelid = 'datagateway.datasource'::regclass AND conname = 'ck_datasource_query_timeout_seconds') THEN
        ALTER TABLE datagateway.datasource ADD CONSTRAINT ck_datasource_query_timeout_seconds
            CHECK (query_timeout_seconds BETWEEN 1 AND 86400);
    END IF;
END
$migration$;
-- Install the reserved expression cells (catalog-expression.sql) and the store_type cells (catalog-store-type.sql)
-- before the current trigger and views name them.
ALTER TABLE datagateway.datasourcecolumnaudit ADD COLUMN IF NOT EXISTS expression text;
ALTER TABLE datagateway.datasourcecolumn ADD COLUMN IF NOT EXISTS expression text;
ALTER TABLE datagateway.datasourcecolumnaudit ADD COLUMN IF NOT EXISTS store_type varchar(128);
ALTER TABLE datagateway.datasourcecolumn ADD COLUMN IF NOT EXISTS store_type varchar(128);
ALTER TABLE datagateway.datasourcecolumnaudit ADD COLUMN IF NOT EXISTS store_type_override boolean NOT NULL DEFAULT false;
ALTER TABLE datagateway.datasourcecolumn ADD COLUMN IF NOT EXISTS store_type_override boolean NOT NULL DEFAULT false;
-- Refresh the common trigger definitions and explicit read projections.
\ir catalog-ddl.sql
\ir catalog-views.sql
