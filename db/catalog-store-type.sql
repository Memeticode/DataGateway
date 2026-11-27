-- Add each catalog column's store_type and store_type_override and preserve the existing catalog audit history.
-- Run with sqlcmd -i db/catalog-store-type.sql -v CatalogDatabase=Catalog before starting the updated DataGateway.
-- store_type is the source database's own spelling of the column's type, such as varchar(20); NULL binds filter
-- values as before. store_type_override (default 0) keeps store_type even where the source's view disagrees.
-- It also adds each column's reserved expression (catalog-expression.sql), because the trigger and view below name
-- it; either script, run in either order or again, leaves the same catalog. Rerunnable.
:On Error exit
USE [$(CatalogDatabase)]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
SET XACT_ABORT ON;
-- Wait at most 10 seconds for a lock held by a running reader or writer; the error then rolls everything back.
SET LOCK_TIMEOUT 10000;
BEGIN TRY
    BEGIN TRAN;
    IF COL_LENGTH(N'dbo.DataSourceColumn', N'expression') IS NULL
        ALTER TABLE [dbo].[DataSourceColumn] ADD [expression] [nvarchar](max) NULL;

    IF COL_LENGTH(N'dbo.DataSourceColumnAudit', N'expression') IS NULL
        ALTER TABLE [dbo].[DataSourceColumnAudit] ADD [expression] [nvarchar](max) NULL;

    IF COL_LENGTH(N'dbo.DataSourceColumn', N'store_type') IS NULL
        ALTER TABLE [dbo].[DataSourceColumn] ADD [store_type] [nvarchar](128) NULL;

    IF COL_LENGTH(N'dbo.DataSourceColumnAudit', N'store_type') IS NULL
        ALTER TABLE [dbo].[DataSourceColumnAudit] ADD [store_type] [nvarchar](128) NULL;

    IF COL_LENGTH(N'dbo.DataSourceColumn', N'store_type_override') IS NULL
        ALTER TABLE [dbo].[DataSourceColumn] ADD [store_type_override] [bit] NOT NULL
            CONSTRAINT [DF_DataSourceColumn_store_type_override] DEFAULT ((0));

    IF COL_LENGTH(N'dbo.DataSourceColumnAudit', N'store_type_override') IS NULL
        ALTER TABLE [dbo].[DataSourceColumnAudit] ADD [store_type_override] [bit] NOT NULL
            CONSTRAINT [DF_DataSourceColumnAudit_store_type_override] DEFAULT ((0));

    EXEC(N'CREATE OR ALTER TRIGGER [dbo].[trDataSourceColumn_Audit] ON [dbo].[DataSourceColumn]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
	SET NOCOUNT ON;
	-- insert or update: the row as it now is (id is an identity, so it joins inserted to deleted)
	INSERT INTO [dbo].[DataSourceColumnAudit] (
		[audit_action]
		, [datasourcecolumn_fk]
		, [datasource_fk]
		, [column_name]
		, [ordinal]
		, [title]
		, [description]
		, [data_type]
		, [unit]
		, [magnitude]
		, [display_format]
		, [can_select]
		, [can_filter]
		, [can_sort]
		, [can_group]
		, [can_aggregate]
		, [expression]
		, [store_type]
		, [store_type_override]
	)
	SELECT
		CASE WHEN d.[id] IS NULL THEN ''I'' ELSE ''U'' END
		, i.[id]
		, i.[datasource_fk]
		, i.[column_name]
		, i.[ordinal]
		, i.[title]
		, i.[description]
		, i.[data_type]
		, i.[unit]
		, i.[magnitude]
		, i.[display_format]
		, i.[can_select]
		, i.[can_filter]
		, i.[can_sort]
		, i.[can_group]
		, i.[can_aggregate]
		, i.[expression]
		, i.[store_type]
		, i.[store_type_override]
	FROM inserted AS i
	LEFT JOIN deleted AS d ON d.[id] = i.[id];
	-- delete: the row as it was
	INSERT INTO [dbo].[DataSourceColumnAudit] (
		[audit_action]
		, [datasourcecolumn_fk]
		, [datasource_fk]
		, [column_name]
		, [ordinal]
		, [title]
		, [description]
		, [data_type]
		, [unit]
		, [magnitude]
		, [display_format]
		, [can_select]
		, [can_filter]
		, [can_sort]
		, [can_group]
		, [can_aggregate]
		, [expression]
		, [store_type]
		, [store_type_override]
	)
	SELECT
		''D''
		, d.[id]
		, d.[datasource_fk]
		, d.[column_name]
		, d.[ordinal]
		, d.[title]
		, d.[description]
		, d.[data_type]
		, d.[unit]
		, d.[magnitude]
		, d.[display_format]
		, d.[can_select]
		, d.[can_filter]
		, d.[can_sort]
		, d.[can_group]
		, d.[can_aggregate]
		, d.[expression]
		, d.[store_type]
		, d.[store_type_override]
	FROM deleted AS d
	WHERE NOT EXISTS (SELECT 1 FROM inserted AS i WHERE i.[id] = d.[id]);
END');
    -- ALTER keeps the view's existing grants.
    EXEC(N'CREATE OR ALTER VIEW [dbo].[vDataSourceColumn] AS
SELECT [datasource_fk], [column_name], [ordinal], [title], [description], [data_type], [unit], [magnitude],
       [display_format], [can_select], [can_filter], [can_sort], [can_group], [can_aggregate], [expression], [store_type],
       [store_type_override]
FROM [dbo].[DataSourceColumn];');
    COMMIT TRAN;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRAN;
    THROW;
END CATCH
GO
