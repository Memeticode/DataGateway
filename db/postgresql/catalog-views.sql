\set ON_ERROR_STOP on
CREATE OR REPLACE VIEW datagateway.vdatasource AS
SELECT id, name, connection_string, object_name, title, description, grain, is_active, row_cap, query_timeout_seconds
FROM datagateway.datasource;
CREATE OR REPLACE VIEW datagateway.vdatasourcecolumn AS
SELECT datasource_fk, column_name, ordinal, title, description, data_type, unit, magnitude,
       display_format, can_select, can_filter, can_sort, can_group, can_aggregate, expression, store_type,
       store_type_override
FROM datagateway.datasourcecolumn;
CREATE OR REPLACE VIEW datagateway.vmcpserver AS
SELECT id, title, about
FROM datagateway.mcpserver;
CREATE OR REPLACE VIEW datagateway.vmcptool AS
SELECT tool_name, title, description
FROM datagateway.mcptool;
