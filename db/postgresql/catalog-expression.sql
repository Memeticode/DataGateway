\set ON_ERROR_STOP on
-- Reserve each catalog column's expression and preserve audit history. Rerunnable.
-- Run it on its own (psql -f, without -1): this script owns its single transaction.
-- The column is reserved: DataGateway refuses a catalog column whose expression is not null.
-- It also adds each column's store_type and store_type_override (catalog-store-type.sql), because the function and view below name it;
-- either script, run in either order or again, leaves the same catalog.
-- It replaces only the column audit function and the column view, the same text as catalog-ddl.sql and
-- catalog-views.sql, and changes no grant: CREATE OR REPLACE keeps each object's existing privileges.
BEGIN;
-- Wait at most 10 seconds for a lock held by a running reader or writer; the error then rolls everything back.
SET LOCAL lock_timeout = '10s';
-- Install the nullable audit cells before the refreshed function names them.
ALTER TABLE datagateway.datasourcecolumnaudit ADD COLUMN IF NOT EXISTS expression text;
ALTER TABLE datagateway.datasourcecolumn ADD COLUMN IF NOT EXISTS expression text;
ALTER TABLE datagateway.datasourcecolumnaudit ADD COLUMN IF NOT EXISTS store_type varchar(128);
ALTER TABLE datagateway.datasourcecolumn ADD COLUMN IF NOT EXISTS store_type varchar(128);
ALTER TABLE datagateway.datasourcecolumnaudit ADD COLUMN IF NOT EXISTS store_type_override boolean NOT NULL DEFAULT false;
ALTER TABLE datagateway.datasourcecolumn ADD COLUMN IF NOT EXISTS store_type_override boolean NOT NULL DEFAULT false;
CREATE OR REPLACE FUNCTION datagateway.audit_datasourcecolumn() RETURNS trigger
LANGUAGE plpgsql SECURITY DEFINER
SET search_path = pg_catalog, datagateway, pg_temp
AS $audit$
DECLARE image datagateway.datasourcecolumn%ROWTYPE;
BEGIN
    IF TG_OP = 'DELETE' THEN image := OLD; ELSE image := NEW; END IF;
    INSERT INTO datagateway.datasourcecolumnaudit
        (audit_action, audit_date, audit_login, datasourcecolumn_fk, datasource_fk, column_name, ordinal, title, description, data_type, unit, magnitude, display_format, can_select, can_filter, can_sort, can_group, can_aggregate, expression, store_type, store_type_override)
    VALUES (substr(TG_OP,1,1), clock_timestamp(), session_user,
        image.id, image.datasource_fk, image.column_name, image.ordinal, image.title, image.description, image.data_type, image.unit, image.magnitude, image.display_format, image.can_select, image.can_filter, image.can_sort, image.can_group, image.can_aggregate, image.expression, image.store_type, image.store_type_override);
    RETURN NULL;
END
$audit$;
-- expression, store_type and store_type_override are appended last, so CREATE OR REPLACE VIEW keeps every existing column in place.
CREATE OR REPLACE VIEW datagateway.vdatasourcecolumn AS
SELECT datasource_fk, column_name, ordinal, title, description, data_type, unit, magnitude,
       display_format, can_select, can_filter, can_sort, can_group, can_aggregate, expression, store_type,
       store_type_override
FROM datagateway.datasourcecolumn;
COMMIT;
