\set ON_ERROR_STOP on
-- query_timeout_seconds is omitted to use the catalog default of 30 seconds.
-- Prerequisite: synthetic.items already exposes id/label/amount/day/flag.
-- SourceConnection is a password-free localhost connection supplied as a psql value.
BEGIN;
CREATE TEMP TABLE dg_example_connection (value text) ON COMMIT DROP;
INSERT INTO dg_example_connection VALUES (:'SourceConnection');
DO $seed$
DECLARE item record; parent integer;
BEGIN
    IF EXISTS (SELECT 1 FROM dg_example_connection WHERE value ~* '(^|;)\s*(password|pwd)\s*=') THEN
        RAISE EXCEPTION 'SourceConnection must be password-free';
    END IF;
    FOR item IN SELECT 'synthetic_items'::text AS name, value AS connection_string,
        'synthetic.items'::text AS object_name, 'Synthetic items'::text AS title,
        'Synthetic rows for PostgreSQL setup.'::text AS description,
        'One row per item.'::text AS grain, true AS is_active, 500 AS row_cap FROM dg_example_connection LOOP
        INSERT INTO datagateway.datasource AS target (name, connection_string, object_name, title, description, grain, is_active, row_cap)
        VALUES (item.name, item.connection_string, item.object_name, item.title, item.description, item.grain, item.is_active, item.row_cap)
        ON CONFLICT (lower(name COLLATE "C")) DO UPDATE SET
            name = EXCLUDED.name, connection_string = EXCLUDED.connection_string, object_name = EXCLUDED.object_name, title = EXCLUDED.title, description = EXCLUDED.description, grain = EXCLUDED.grain, is_active = EXCLUDED.is_active, row_cap = EXCLUDED.row_cap
        WHERE (target.name, target.connection_string, target.object_name, target.title, target.description, target.grain, target.is_active, target.row_cap)
            IS DISTINCT FROM (EXCLUDED.name, EXCLUDED.connection_string, EXCLUDED.object_name, EXCLUDED.title, EXCLUDED.description, EXCLUDED.grain, EXCLUDED.is_active, EXCLUDED.row_cap);
    END LOOP;
    SELECT id INTO STRICT parent FROM datagateway.datasource WHERE lower(name COLLATE "C") = 'synthetic_items';
    FOR item IN SELECT parent AS datasource_fk, v.column_name, v.ordinal, v.column_name AS title,
        'Column ' || v.column_name || '.' AS description, v.data_type, NULL::text AS unit,
        NULL::text AS magnitude, NULL::text AS display_format,
        true AS can_select, true AS can_filter, true AS can_sort, true AS can_group,
        v.data_type <> 'bool' AS can_aggregate
        FROM (VALUES ('id',1,'number'),('label',2,'text'),('amount',3,'number'),('day',4,'date'),('flag',5,'bool'))
        AS v(column_name,ordinal,data_type) LOOP
        INSERT INTO datagateway.datasourcecolumn AS target (datasource_fk, column_name, ordinal, title, description, data_type, unit, magnitude, display_format, can_select, can_filter, can_sort, can_group, can_aggregate)
        VALUES (item.datasource_fk, item.column_name, item.ordinal, item.title, item.description, item.data_type, item.unit, item.magnitude, item.display_format, item.can_select, item.can_filter, item.can_sort, item.can_group, item.can_aggregate)
        ON CONFLICT (datasource_fk, lower(column_name COLLATE "C")) DO UPDATE SET
            datasource_fk = EXCLUDED.datasource_fk, column_name = EXCLUDED.column_name, ordinal = EXCLUDED.ordinal, title = EXCLUDED.title, description = EXCLUDED.description, data_type = EXCLUDED.data_type, unit = EXCLUDED.unit, magnitude = EXCLUDED.magnitude, display_format = EXCLUDED.display_format, can_select = EXCLUDED.can_select, can_filter = EXCLUDED.can_filter, can_sort = EXCLUDED.can_sort, can_group = EXCLUDED.can_group, can_aggregate = EXCLUDED.can_aggregate
        WHERE (target.datasource_fk, target.column_name, target.ordinal, target.title, target.description, target.data_type, target.unit, target.magnitude, target.display_format, target.can_select, target.can_filter, target.can_sort, target.can_group, target.can_aggregate)
            IS DISTINCT FROM (EXCLUDED.datasource_fk, EXCLUDED.column_name, EXCLUDED.ordinal, EXCLUDED.title, EXCLUDED.description, EXCLUDED.data_type, EXCLUDED.unit, EXCLUDED.magnitude, EXCLUDED.display_format, EXCLUDED.can_select, EXCLUDED.can_filter, EXCLUDED.can_sort, EXCLUDED.can_group, EXCLUDED.can_aggregate);
    END LOOP;
END
$seed$;
COMMIT;
