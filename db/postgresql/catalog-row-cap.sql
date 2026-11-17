\set ON_ERROR_STOP on
-- Preserve audit history. The caller owns the transaction.
-- Install the nullable audit cell before any backfill can fire a refreshed trigger.
ALTER TABLE datagateway.datasourceaudit ADD COLUMN IF NOT EXISTS row_cap integer;
ALTER TABLE datagateway.datasource ADD COLUMN IF NOT EXISTS row_cap integer DEFAULT 500;
UPDATE datagateway.datasource SET row_cap = 500 WHERE row_cap IS NULL;
ALTER TABLE datagateway.datasource ALTER COLUMN row_cap SET DEFAULT 500;
ALTER TABLE datagateway.datasource ALTER COLUMN row_cap SET NOT NULL;
DO $migration$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_constraint
        WHERE conrelid = 'datagateway.datasource'::regclass AND conname = 'ck_datasource_row_cap') THEN
        ALTER TABLE datagateway.datasource ADD CONSTRAINT ck_datasource_row_cap
            CHECK (row_cap BETWEEN 1 AND 2147483646);
    END IF;
END
$migration$;
-- Refresh the common trigger definitions and explicit read projections.
-- Install the detail-only timeout before the current views project it.
\ir catalog-query-timeout.sql
\ir catalog-views.sql
