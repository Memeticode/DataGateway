-- Example catalog registration: edit the source name, view, metadata and column rows for your view.
-- Create the view first; this script creates no object and inserts no view data.
-- Run from the public root as in README, Get started, step 4:
-- $instance = 'localhost'
-- $secure = Read-Host 'Source connection' -AsSecureString
-- $sourceConnection = [System.Net.NetworkCredential]::new('', $secure).Password
-- Reject CR/LF, then set $env:SourceConnectionString to $sourceConnection.Replace("'", "''").
-- Remove-Variable sourceConnection
-- try { sqlcmd -S $instance -d Catalog -E -b -r 1 -i <your copy> -v CatalogDatabase=Catalog
--       if ($LASTEXITCODE -ne 0) { throw 'Registering the view failed.' } }
-- finally { Remove-Item Env:SourceConnectionString }
-- Never echo SQL or the input. MERGE natural keys; update only changed rows, preserving audit history.
-- Keep SCOPE_IDENTITY() for new ids and the binary EXCEPT comparisons for exact changes.
-- Keep existing rows in place; every row write must fire its audit triggers.
-- Any returned rowset must go into a table variable when triggers exist.
:On Error exit
USE [$(CatalogDatabase)]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @source_connection_string [varchar](max) = '$(SourceConnectionString)';
IF NULLIF(LTRIM(RTRIM(@source_connection_string)), '') IS NULL
	OR DATALENGTH(@source_connection_string) > 1000
	OR CHARINDEX(CHAR(10), @source_connection_string) > 0
	OR CHARINDEX(CHAR(13), @source_connection_string) > 0
	THROW 50001, 'FIX THE CALL: SourceConnectionString must be nonempty, at most 1000 bytes and one line; supply it through the environment.', 1;

BEGIN TRY
	BEGIN TRAN;

	DECLARE @id [int];
	SELECT @id = [id]
	FROM [dbo].[DataSource] WITH (UPDLOCK, HOLDLOCK)
	WHERE [name] = N'sales_by_line';

	;WITH src ([name], [connection_string], [object_name], [title], [description], [grain], [is_active], [row_cap])
	AS (
		SELECT CONVERT([varchar](128), N'sales_by_line')
			, @source_connection_string
			, CONVERT([varchar](256), N'dbo.vSalesByLine')
			, N'Sales by invoice line'
			, N'One row per invoice line, with the customer country and the line total. Use it for revenue by country. Unlike an invoice summary, an invoice appears once per line, so sum line_total to get invoice totals.'
			, N'one row per invoice line'
			, CONVERT([bit], 1)
			, CONVERT([int], 500)
	)
	MERGE [dbo].[DataSource] WITH (HOLDLOCK) dst
	USING src ON dst.[name] = src.[name]
	WHEN MATCHED AND EXISTS (
			SELECT CONVERT([varbinary](max), dst.[name])
				, CONVERT([varbinary](max), dst.[connection_string])
				, CONVERT([varbinary](max), dst.[object_name])
				, CONVERT([varbinary](max), dst.[title])
				, CONVERT([varbinary](max), dst.[description])
				, CONVERT([varbinary](max), dst.[grain])
				, CONVERT([varbinary](max), dst.[is_active])
				, CONVERT([varbinary](max), dst.[row_cap])
			EXCEPT
			SELECT CONVERT([varbinary](max), src.[name])
				, CONVERT([varbinary](max), src.[connection_string])
				, CONVERT([varbinary](max), src.[object_name])
				, CONVERT([varbinary](max), src.[title])
				, CONVERT([varbinary](max), src.[description])
				, CONVERT([varbinary](max), src.[grain])
				, CONVERT([varbinary](max), src.[is_active])
				, CONVERT([varbinary](max), src.[row_cap])
		)
	THEN UPDATE SET
		[name] = src.[name]
		, [connection_string] = src.[connection_string]
		, [object_name] = src.[object_name]
		, [title] = src.[title]
		, [description] = src.[description]
		, [grain] = src.[grain]
		, [is_active] = src.[is_active]
		, [row_cap] = src.[row_cap]
	WHEN NOT MATCHED BY TARGET THEN
		INSERT ([name], [connection_string], [object_name], [title], [description], [grain], [is_active], [row_cap])
		VALUES (src.[name], src.[connection_string], src.[object_name], src.[title], src.[description], src.[grain], src.[is_active], src.[row_cap]);

	IF @id IS NULL
		SET @id = CONVERT([int], SCOPE_IDENTITY());

	DECLARE @column_seed TABLE (
		[datasource_fk] [int] NOT NULL
		, [column_name] [varchar](128) NOT NULL
		, [ordinal] [int] NOT NULL
		, [title] [nvarchar](200) NOT NULL
		, [description] [nvarchar](max) NOT NULL
		, [data_type] [varchar](10) NOT NULL
		, [unit] [varchar](50) NULL
		, [magnitude] [varchar](10) NULL
		, [display_format] [varchar](50) NULL
		, [can_select] [bit] NOT NULL
		, [can_filter] [bit] NOT NULL
		, [can_sort] [bit] NOT NULL
		, [can_group] [bit] NOT NULL
		, [can_aggregate] [bit] NOT NULL
	);


	INSERT INTO @column_seed ([datasource_fk], [column_name], [ordinal], [title], [description], [data_type], [unit], [magnitude], [display_format], [can_select], [can_filter], [can_sort], [can_group], [can_aggregate])
	VALUES (@id, N'country', 1, N'Country', N'Country of the customer on the invoice.', N'text', NULL, NULL, NULL, 1, 1, 1, 1, 0);

	INSERT INTO @column_seed ([datasource_fk], [column_name], [ordinal], [title], [description], [data_type], [unit], [magnitude], [display_format], [can_select], [can_filter], [can_sort], [can_group], [can_aggregate])
	VALUES (@id, N'line_total', 2, N'Line total', N'Price times quantity for the line, in US dollars.', N'number', N'USD', N'none', N'0.00', 1, 1, 1, 0, 1);

	;WITH src AS (SELECT [datasource_fk], [column_name], [ordinal], [title], [description], [data_type], [unit], [magnitude], [display_format], [can_select], [can_filter], [can_sort], [can_group], [can_aggregate] FROM @column_seed)
	MERGE [dbo].[DataSourceColumn] WITH (HOLDLOCK) dst
	USING src ON dst.[datasource_fk] = src.[datasource_fk]
		AND dst.[column_name] = src.[column_name]
	WHEN MATCHED AND EXISTS (
			SELECT CONVERT([varbinary](max), dst.[column_name])
				, CONVERT([varbinary](max), dst.[ordinal])
				, CONVERT([varbinary](max), dst.[title])
				, CONVERT([varbinary](max), dst.[description])
				, CONVERT([varbinary](max), dst.[data_type])
				, CONVERT([varbinary](max), dst.[unit])
				, CONVERT([varbinary](max), dst.[magnitude])
				, CONVERT([varbinary](max), dst.[display_format])
				, CONVERT([varbinary](max), dst.[can_select])
				, CONVERT([varbinary](max), dst.[can_filter])
				, CONVERT([varbinary](max), dst.[can_sort])
				, CONVERT([varbinary](max), dst.[can_group])
				, CONVERT([varbinary](max), dst.[can_aggregate])
			EXCEPT
			SELECT CONVERT([varbinary](max), src.[column_name])
				, CONVERT([varbinary](max), src.[ordinal])
				, CONVERT([varbinary](max), src.[title])
				, CONVERT([varbinary](max), src.[description])
				, CONVERT([varbinary](max), src.[data_type])
				, CONVERT([varbinary](max), src.[unit])
				, CONVERT([varbinary](max), src.[magnitude])
				, CONVERT([varbinary](max), src.[display_format])
				, CONVERT([varbinary](max), src.[can_select])
				, CONVERT([varbinary](max), src.[can_filter])
				, CONVERT([varbinary](max), src.[can_sort])
				, CONVERT([varbinary](max), src.[can_group])
				, CONVERT([varbinary](max), src.[can_aggregate])
		)
	THEN UPDATE SET
		[column_name] = src.[column_name]
		, [ordinal] = src.[ordinal]
		, [title] = src.[title]
		, [description] = src.[description]
		, [data_type] = src.[data_type]
		, [unit] = src.[unit]
		, [magnitude] = src.[magnitude]
		, [display_format] = src.[display_format]
		, [can_select] = src.[can_select]
		, [can_filter] = src.[can_filter]
		, [can_sort] = src.[can_sort]
		, [can_group] = src.[can_group]
		, [can_aggregate] = src.[can_aggregate]
	WHEN NOT MATCHED BY TARGET THEN
		INSERT ([datasource_fk], [column_name], [ordinal], [title], [description], [data_type], [unit], [magnitude], [display_format], [can_select], [can_filter], [can_sort], [can_group], [can_aggregate])
		VALUES (src.[datasource_fk], src.[column_name], src.[ordinal], src.[title], src.[description], src.[data_type], src.[unit], src.[magnitude], src.[display_format], src.[can_select], src.[can_filter], src.[can_sort], src.[can_group], src.[can_aggregate]);

	COMMIT TRAN;
END TRY
BEGIN CATCH
	IF XACT_STATE() <> 0
		ROLLBACK TRAN;
	THROW;
END CATCH
GO
