\set ON_ERROR_STOP on
-- ReaderRole names an already-existing role. Never creates or alters roles.
GRANT USAGE ON SCHEMA datagateway TO :"ReaderRole";
GRANT SELECT ON datagateway.vdatasource, datagateway.vdatasourcecolumn TO :"ReaderRole";
-- Grant SELECT on the registered source views separately in each source database.
