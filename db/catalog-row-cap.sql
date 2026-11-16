-- Add each source's row cap and preserve the existing catalog audit history.
-- Run with sqlcmd -i db/catalog-row-cap.sql -v CatalogDatabase=Catalog before starting the updated DataGateway.
:On Error exit
USE [$(CatalogDatabase)]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
SET XACT_ABORT ON;
BEGIN TRY
    BEGIN TRAN;
    IF COL_LENGTH(N'dbo.DataSource', N'row_cap') IS NULL
        ALTER TABLE [dbo].[DataSource] ADD [row_cap] [int] NOT NULL
            CONSTRAINT [DF_DataSource_row_cap] DEFAULT ((500)) WITH VALUES;

    IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
        WHERE [parent_object_id] = OBJECT_ID(N'dbo.DataSource') AND [name] = N'CK_DataSource_row_cap')
        EXEC(N'ALTER TABLE [dbo].[DataSource] WITH CHECK ADD CONSTRAINT [CK_DataSource_row_cap]
            CHECK (([row_cap] >= 1 AND [row_cap] <= 2147483646));');

    IF COL_LENGTH(N'dbo.DataSourceAudit', N'row_cap') IS NULL
        ALTER TABLE [dbo].[DataSourceAudit] ADD [row_cap] [int] NULL;

    EXEC(N'CREATE OR ALTER TRIGGER [dbo].[trDataSource_Audit] ON [dbo].[DataSource]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
	SET NOCOUNT ON;
	-- insert or update: the row as it now is (id is an identity, so it joins inserted to deleted)
	INSERT INTO [dbo].[DataSourceAudit] (
		[audit_action]
		, [datasource_fk]
		, [name]
		, [connection_string]
		, [object_name]
		, [title]
		, [description]
		, [grain]
		, [is_active]
		, [row_cap]
	)
	SELECT
		CASE WHEN d.[id] IS NULL THEN ''I'' ELSE ''U'' END
		, i.[id]
		, i.[name]
		, i.[connection_string]
		, i.[object_name]
		, i.[title]
		, i.[description]
		, i.[grain]
		, i.[is_active]
		, i.[row_cap]
	FROM inserted AS i
	LEFT JOIN deleted AS d ON d.[id] = i.[id];
	-- delete: the row as it was
	INSERT INTO [dbo].[DataSourceAudit] (
		[audit_action]
		, [datasource_fk]
		, [name]
		, [connection_string]
		, [object_name]
		, [title]
		, [description]
		, [grain]
		, [is_active]
		, [row_cap]
	)
	SELECT
		''D''
		, d.[id]
		, d.[name]
		, d.[connection_string]
		, d.[object_name]
		, d.[title]
		, d.[description]
		, d.[grain]
		, d.[is_active]
		, d.[row_cap]
	FROM deleted AS d
	WHERE NOT EXISTS (SELECT 1 FROM inserted AS i WHERE i.[id] = d.[id]);
END');
    EXEC(N'CREATE OR ALTER VIEW [dbo].[vDataSource] AS
SELECT [id], [name], [connection_string], [object_name], [title], [description], [grain], [is_active], [row_cap]
FROM [dbo].[DataSource];');
    COMMIT TRAN;
END TRY
BEGIN CATCH
    IF XACT_STATE() <> 0
        ROLLBACK TRAN;
    THROW;
END CATCH
GO
