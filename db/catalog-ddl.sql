-- DataGateway catalog schema; Catalog is the setup default.
-- Design: schema dbo; no COLLATE (the database is case-insensitive); CHECK constraints; varchar for names, nvarchar
-- for descriptions; recovery SIMPLE; an audit table per table, filled by triggers on insert, update and delete,
-- holding the whole row plus the action, the time and the SQL Server login that made the change.
-- Style: lowercase snake_case columns, [id] int IDENTITY clustered, <table>_fk, PK_/FK_/DF_/UQ_ names with PascalCase
-- table and lowercase column.
-- Run separately first (cannot run inside a transaction):  ALTER DATABASE [$(CatalogDatabase)] SET RECOVERY SIMPLE;

USE [$(CatalogDatabase)]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO

CREATE TABLE [dbo].[DataSource](
	[id] [int] IDENTITY(1,1) NOT NULL,
	[name] [varchar](128) NOT NULL,
	[connection_string] [varchar](1000) NOT NULL,  -- the full connection string the source requires, credentials included; never returned or logged
	[object_name] [varchar](256) NOT NULL,         -- schema.view
	[title] [nvarchar](200) NOT NULL,
	[description] [nvarchar](max) NOT NULL,        -- what it is for, when to use it, how it differs
	[grain] [nvarchar](400) NOT NULL,              -- what one row is
	[is_active] [bit] NOT NULL,
	[row_cap] [int] NOT NULL,
	[query_timeout_seconds] [int] NOT NULL,
 CONSTRAINT [PK_DataSource] PRIMARY KEY CLUSTERED ([id] ASC),
 CONSTRAINT [UQ_DataSource_name] UNIQUE NONCLUSTERED ([name] ASC)
)
GO
ALTER TABLE [dbo].[DataSource] ADD CONSTRAINT [DF_DataSource_is_active] DEFAULT ((1)) FOR [is_active]
GO
ALTER TABLE [dbo].[DataSource] ADD CONSTRAINT [DF_DataSource_row_cap] DEFAULT ((500)) FOR [row_cap]
GO
ALTER TABLE [dbo].[DataSource] ADD CONSTRAINT [DF_DataSource_query_timeout_seconds] DEFAULT ((30)) FOR [query_timeout_seconds]
GO
ALTER TABLE [dbo].[DataSource] WITH CHECK ADD CONSTRAINT [CK_DataSource_query_timeout_seconds] CHECK (([query_timeout_seconds] BETWEEN 1 AND 86400))
GO
ALTER TABLE [dbo].[DataSource] WITH CHECK ADD CONSTRAINT [CK_DataSource_row_cap] CHECK (([row_cap] >= 1 AND [row_cap] <= 2147483646))
GO

CREATE TABLE [dbo].[DataSourceColumn](
	[id] [int] IDENTITY(1,1) NOT NULL,
	[datasource_fk] [int] NOT NULL,
	[column_name] [varchar](128) NOT NULL,
	[ordinal] [int] NOT NULL,
	[title] [nvarchar](200) NOT NULL,
	[description] [nvarchar](max) NOT NULL,
	[data_type] [varchar](10) NOT NULL,
	[unit] [varchar](50) NULL,
	[magnitude] [varchar](10) NULL,
	[display_format] [varchar](50) NULL,
	[can_select] [bit] NOT NULL,
	[can_filter] [bit] NOT NULL,
	[can_sort] [bit] NOT NULL,
	[can_group] [bit] NOT NULL,
	[can_aggregate] [bit] NOT NULL,
 CONSTRAINT [PK_DataSourceColumn] PRIMARY KEY CLUSTERED ([id] ASC),
 CONSTRAINT [UQ_DataSourceColumn_datasource_column] UNIQUE NONCLUSTERED ([datasource_fk] ASC, [column_name] ASC)
)
GO
ALTER TABLE [dbo].[DataSourceColumn] ADD CONSTRAINT [DF_DataSourceColumn_can_select] DEFAULT ((1)) FOR [can_select]
GO
ALTER TABLE [dbo].[DataSourceColumn] ADD CONSTRAINT [DF_DataSourceColumn_can_filter] DEFAULT ((0)) FOR [can_filter]
GO
ALTER TABLE [dbo].[DataSourceColumn] ADD CONSTRAINT [DF_DataSourceColumn_can_sort] DEFAULT ((0)) FOR [can_sort]
GO
ALTER TABLE [dbo].[DataSourceColumn] ADD CONSTRAINT [DF_DataSourceColumn_can_group] DEFAULT ((0)) FOR [can_group]
GO
ALTER TABLE [dbo].[DataSourceColumn] ADD CONSTRAINT [DF_DataSourceColumn_can_aggregate] DEFAULT ((0)) FOR [can_aggregate]
GO
ALTER TABLE [dbo].[DataSourceColumn] WITH CHECK ADD CONSTRAINT [FK_DataSourceColumn_DataSource] FOREIGN KEY([datasource_fk])
REFERENCES [dbo].[DataSource] ([id])
GO
ALTER TABLE [dbo].[DataSourceColumn] WITH CHECK ADD CONSTRAINT [CK_DataSourceColumn_data_type] CHECK (([data_type] IN ('text', 'number', 'date', 'bool')))
GO
ALTER TABLE [dbo].[DataSourceColumn] WITH CHECK ADD CONSTRAINT [CK_DataSourceColumn_magnitude] CHECK (([magnitude] IN ('none', 'thousands', 'millions', 'billions')))
GO

-- Audit: one table per table; every insert and update stores the row as it now is, every delete the row as it was.
-- The *_fk columns carry no declared foreign key on purpose: audit rows outlive deleted rows.
CREATE TABLE [dbo].[DataSourceAudit](
	[id] [int] IDENTITY(1,1) NOT NULL,
	[audit_action] [char](1) NOT NULL,              -- I insert, U update, D delete
	[audit_date] [datetime] NOT NULL,
	[audit_login] [varchar](128) NOT NULL,          -- the login that connected and made the change (ORIGINAL_LOGIN)
	[datasource_fk] [int] NOT NULL,
	[name] [varchar](128) NOT NULL,
	[connection_string] [varchar](1000) NOT NULL,
	[object_name] [varchar](256) NOT NULL,
	[title] [nvarchar](200) NOT NULL,
	[description] [nvarchar](max) NOT NULL,
	[grain] [nvarchar](400) NOT NULL,
	[is_active] [bit] NOT NULL,
	[row_cap] [int] NULL,
	[query_timeout_seconds] [int] NULL,
 CONSTRAINT [PK_DataSourceAudit] PRIMARY KEY CLUSTERED ([id] ASC)
)
GO
ALTER TABLE [dbo].[DataSourceAudit] ADD CONSTRAINT [DF_DataSourceAudit_audit_date] DEFAULT (getdate()) FOR [audit_date]
GO
ALTER TABLE [dbo].[DataSourceAudit] ADD CONSTRAINT [DF_DataSourceAudit_audit_login] DEFAULT (original_login()) FOR [audit_login]
GO
ALTER TABLE [dbo].[DataSourceAudit] WITH CHECK ADD CONSTRAINT [CK_DataSourceAudit_audit_action] CHECK (([audit_action] IN ('I', 'U', 'D')))
GO

CREATE TABLE [dbo].[DataSourceColumnAudit](
	[id] [int] IDENTITY(1,1) NOT NULL,
	[audit_action] [char](1) NOT NULL,
	[audit_date] [datetime] NOT NULL,
	[audit_login] [varchar](128) NOT NULL,
	[datasourcecolumn_fk] [int] NOT NULL,
	[datasource_fk] [int] NOT NULL,
	[column_name] [varchar](128) NOT NULL,
	[ordinal] [int] NOT NULL,
	[title] [nvarchar](200) NOT NULL,
	[description] [nvarchar](max) NOT NULL,
	[data_type] [varchar](10) NOT NULL,
	[unit] [varchar](50) NULL,
	[magnitude] [varchar](10) NULL,
	[display_format] [varchar](50) NULL,
	[can_select] [bit] NOT NULL,
	[can_filter] [bit] NOT NULL,
	[can_sort] [bit] NOT NULL,
	[can_group] [bit] NOT NULL,
	[can_aggregate] [bit] NOT NULL,
 CONSTRAINT [PK_DataSourceColumnAudit] PRIMARY KEY CLUSTERED ([id] ASC)
)
GO
ALTER TABLE [dbo].[DataSourceColumnAudit] ADD CONSTRAINT [DF_DataSourceColumnAudit_audit_date] DEFAULT (getdate()) FOR [audit_date]
GO
ALTER TABLE [dbo].[DataSourceColumnAudit] ADD CONSTRAINT [DF_DataSourceColumnAudit_audit_login] DEFAULT (original_login()) FOR [audit_login]
GO
ALTER TABLE [dbo].[DataSourceColumnAudit] WITH CHECK ADD CONSTRAINT [CK_DataSourceColumnAudit_audit_action] CHECK (([audit_action] IN ('I', 'U', 'D')))
GO

-- =============================================
-- Author:		DataGateway
-- Create date: 2026-10-30
-- Description:	Audit every insert, update and delete on dbo.DataSource into dbo.DataSourceAudit
-- =============================================
CREATE TRIGGER [dbo].[trDataSource_Audit] ON [dbo].[DataSource]
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
		, [query_timeout_seconds]
	)
	SELECT
		CASE WHEN d.[id] IS NULL THEN 'I' ELSE 'U' END
		, i.[id]
		, i.[name]
		, i.[connection_string]
		, i.[object_name]
		, i.[title]
		, i.[description]
		, i.[grain]
		, i.[is_active]
		, i.[row_cap]
		, i.[query_timeout_seconds]
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
		, [query_timeout_seconds]
	)
	SELECT
		'D'
		, d.[id]
		, d.[name]
		, d.[connection_string]
		, d.[object_name]
		, d.[title]
		, d.[description]
		, d.[grain]
		, d.[is_active]
		, d.[row_cap]
		, d.[query_timeout_seconds]
	FROM deleted AS d
	WHERE NOT EXISTS (SELECT 1 FROM inserted AS i WHERE i.[id] = d.[id]);
END
GO

-- =============================================
-- Author:		DataGateway
-- Create date: 2026-10-30
-- Description:	Audit every insert, update and delete on dbo.DataSourceColumn into dbo.DataSourceColumnAudit
-- =============================================
CREATE TRIGGER [dbo].[trDataSourceColumn_Audit] ON [dbo].[DataSourceColumn]
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
	)
	SELECT
		CASE WHEN d.[id] IS NULL THEN 'I' ELSE 'U' END
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
	)
	SELECT
		'D'
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
	FROM deleted AS d
	WHERE NOT EXISTS (SELECT 1 FROM inserted AS i WHERE i.[id] = d.[id]);
END
GO
