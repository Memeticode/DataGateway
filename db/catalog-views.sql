-- The shape DataGateway reads from the catalog: storage may change underneath while these columns hold. Rerunnable.
USE [$(CatalogDatabase)]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
CREATE OR ALTER VIEW [dbo].[vDataSource] AS
SELECT [id], [name], [connection_string], [object_name], [title], [description], [grain], [is_active], [row_cap]
FROM [dbo].[DataSource];
GO
CREATE OR ALTER VIEW [dbo].[vDataSourceColumn] AS
SELECT [datasource_fk], [column_name], [ordinal], [title], [description], [data_type], [unit], [magnitude],
       [display_format], [can_select], [can_filter], [can_sort], [can_group], [can_aggregate]
FROM [dbo].[DataSourceColumn];
GO
