-- DataGateway read-only identity.
--
-- Creates the SQL login `gateway_reader`. In the catalog database and in one source database it gets db_datareader
-- and db_denydatawriter. Run it once for each source database; the guards make a second run safe.
--
--   sqlcmd -S <server> -E -i gateway-reader.sql -v CatalogDatabase=Catalog SourceDatabase=<source database> ReaderPassword=<password>
--
-- db_datareader can read every table and view in those databases. DataGateway itself only ever reads the views and
-- columns registered in the catalog, so the catalog is the boundary of what callers see.
--
-- db_denydatawriter denies INSERT, UPDATE and DELETE on every table, and a deny wins over any grant. So even if a
-- writer role is granted later by mistake, or a query runs as dynamic SQL, this login still cannot change data.
--
-- In production, prefer a gMSA with integrated security, so that no password exists anywhere. If you want a second
-- key at the database level, grant SELECT per registered view instead of db_datareader.

USE [master]
GO
IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'gateway_reader')
    CREATE LOGIN [gateway_reader] WITH PASSWORD = N'$(ReaderPassword)', DEFAULT_DATABASE = [master], CHECK_EXPIRATION = OFF, CHECK_POLICY = ON
GO
USE [$(CatalogDatabase)]
GO
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'gateway_reader')
    CREATE USER [gateway_reader] FOR LOGIN [gateway_reader]
GO
ALTER ROLE [db_datareader] ADD MEMBER [gateway_reader]
GO
ALTER ROLE [db_denydatawriter] ADD MEMBER [gateway_reader]
GO
USE [$(SourceDatabase)]
GO
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'gateway_reader')
    CREATE USER [gateway_reader] FOR LOGIN [gateway_reader]
GO
ALTER ROLE [db_datareader] ADD MEMBER [gateway_reader]
GO
ALTER ROLE [db_denydatawriter] ADD MEMBER [gateway_reader]
GO
