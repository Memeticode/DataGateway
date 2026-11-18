-- Idempotent preview.5 upgrade. Existing presentation text is preserved.
USE [$(CatalogDatabase)]
GO
SET ANSI_NULLS ON
GO
SET QUOTED_IDENTIFIER ON
GO
IF OBJECT_ID(N'dbo.McpServer', N'U') IS NULL
BEGIN
CREATE TABLE [dbo].[McpServer](
    [id] int NOT NULL,
    [title] nvarchar(80) NOT NULL,
    [about] nvarchar(1000) NOT NULL,
    CONSTRAINT [PK_McpServer] PRIMARY KEY CLUSTERED ([id]),
    CONSTRAINT [CK_McpServer_id] CHECK ([id] = 1),
    CONSTRAINT [CK_McpServer_title] CHECK (DATALENGTH([title]) / 2 BETWEEN 1 AND 80 AND LEN(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE([title] COLLATE Latin1_General_100_BIN2, NCHAR(9), N''), NCHAR(10), N''), NCHAR(11), N''), NCHAR(12), N''), NCHAR(13), N''), NCHAR(32), N''), NCHAR(133), N''), NCHAR(160), N''), NCHAR(5760), N''), NCHAR(8192), N''), NCHAR(8193), N''), NCHAR(8194), N''), NCHAR(8195), N''), NCHAR(8196), N''), NCHAR(8197), N''), NCHAR(8198), N''), NCHAR(8199), N''), NCHAR(8200), N''), NCHAR(8201), N''), NCHAR(8202), N''), NCHAR(8232), N''), NCHAR(8233), N''), NCHAR(8239), N''), NCHAR(8287), N''), NCHAR(12288), N'')) > 0 AND CHARINDEX(CHAR(10), [title]) = 0 AND CHARINDEX(CHAR(13), [title]) = 0),
    CONSTRAINT [CK_McpServer_about] CHECK (DATALENGTH([about]) / 2 BETWEEN 1 AND 1000 AND LEN(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE([about] COLLATE Latin1_General_100_BIN2, NCHAR(9), N''), NCHAR(10), N''), NCHAR(11), N''), NCHAR(12), N''), NCHAR(13), N''), NCHAR(32), N''), NCHAR(133), N''), NCHAR(160), N''), NCHAR(5760), N''), NCHAR(8192), N''), NCHAR(8193), N''), NCHAR(8194), N''), NCHAR(8195), N''), NCHAR(8196), N''), NCHAR(8197), N''), NCHAR(8198), N''), NCHAR(8199), N''), NCHAR(8200), N''), NCHAR(8201), N''), NCHAR(8202), N''), NCHAR(8232), N''), NCHAR(8233), N''), NCHAR(8239), N''), NCHAR(8287), N''), NCHAR(12288), N'')) > 0)
);
END
GO
IF OBJECT_ID(N'dbo.McpServerAudit', N'U') IS NULL
BEGIN
CREATE TABLE [dbo].[McpServerAudit](
    [id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_McpServerAudit] PRIMARY KEY CLUSTERED,
    [audit_action] char(1) NOT NULL CONSTRAINT [CK_McpServerAudit_audit_action] CHECK ([audit_action] IN ('I','U','D')),
    [audit_date] datetime NOT NULL CONSTRAINT [DF_McpServerAudit_audit_date] DEFAULT (getdate()),
    [audit_login] varchar(128) NOT NULL CONSTRAINT [DF_McpServerAudit_audit_login] DEFAULT (original_login()),
    [mcpserver_fk] int NOT NULL,
    [title] nvarchar(80) NOT NULL,
    [about] nvarchar(1000) NOT NULL
);
END
GO
CREATE OR ALTER TRIGGER [dbo].[trMcpServer_Audit] ON [dbo].[McpServer]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO [dbo].[McpServerAudit] ([audit_action], [mcpserver_fk], [title], [about])
    SELECT CASE WHEN d.[id] IS NULL THEN 'I' ELSE 'U' END, i.[id], i.[title], i.[about]
    FROM inserted AS i LEFT JOIN deleted AS d ON i.[id] = d.[id];
    INSERT INTO [dbo].[McpServerAudit] ([audit_action], [mcpserver_fk], [title], [about])
    SELECT 'D', d.[id], d.[title], d.[about]
    FROM deleted AS d LEFT JOIN inserted AS i ON i.[id] = d.[id] WHERE i.[id] IS NULL;
END
GO
IF OBJECT_ID(N'dbo.McpTool', N'U') IS NULL
BEGIN
CREATE TABLE [dbo].[McpTool](
    [tool_name] varchar(32) NOT NULL,
    [title] nvarchar(80) NOT NULL,
    [description] nvarchar(1000) NOT NULL,
    CONSTRAINT [PK_McpTool] PRIMARY KEY CLUSTERED ([tool_name]),
    CONSTRAINT [CK_McpTool_tool_name] CHECK ([tool_name] COLLATE Latin1_General_100_BIN2 IN ('list_sources', 'describe_source', 'query_source') AND DATALENGTH([tool_name]) = LEN([tool_name])),
    CONSTRAINT [CK_McpTool_title] CHECK (DATALENGTH([title]) / 2 BETWEEN 1 AND 80 AND LEN(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE([title] COLLATE Latin1_General_100_BIN2, NCHAR(9), N''), NCHAR(10), N''), NCHAR(11), N''), NCHAR(12), N''), NCHAR(13), N''), NCHAR(32), N''), NCHAR(133), N''), NCHAR(160), N''), NCHAR(5760), N''), NCHAR(8192), N''), NCHAR(8193), N''), NCHAR(8194), N''), NCHAR(8195), N''), NCHAR(8196), N''), NCHAR(8197), N''), NCHAR(8198), N''), NCHAR(8199), N''), NCHAR(8200), N''), NCHAR(8201), N''), NCHAR(8202), N''), NCHAR(8232), N''), NCHAR(8233), N''), NCHAR(8239), N''), NCHAR(8287), N''), NCHAR(12288), N'')) > 0 AND CHARINDEX(CHAR(10), [title]) = 0 AND CHARINDEX(CHAR(13), [title]) = 0),
    CONSTRAINT [CK_McpTool_description] CHECK (DATALENGTH([description]) / 2 BETWEEN 1 AND 1000 AND LEN(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE([description] COLLATE Latin1_General_100_BIN2, NCHAR(9), N''), NCHAR(10), N''), NCHAR(11), N''), NCHAR(12), N''), NCHAR(13), N''), NCHAR(32), N''), NCHAR(133), N''), NCHAR(160), N''), NCHAR(5760), N''), NCHAR(8192), N''), NCHAR(8193), N''), NCHAR(8194), N''), NCHAR(8195), N''), NCHAR(8196), N''), NCHAR(8197), N''), NCHAR(8198), N''), NCHAR(8199), N''), NCHAR(8200), N''), NCHAR(8201), N''), NCHAR(8202), N''), NCHAR(8232), N''), NCHAR(8233), N''), NCHAR(8239), N''), NCHAR(8287), N''), NCHAR(12288), N'')) > 0)
);
END
GO
IF OBJECT_ID(N'dbo.McpToolAudit', N'U') IS NULL
BEGIN
CREATE TABLE [dbo].[McpToolAudit](
    [id] int IDENTITY(1,1) NOT NULL CONSTRAINT [PK_McpToolAudit] PRIMARY KEY CLUSTERED,
    [audit_action] char(1) NOT NULL CONSTRAINT [CK_McpToolAudit_audit_action] CHECK ([audit_action] IN ('I','U','D')),
    [audit_date] datetime NOT NULL CONSTRAINT [DF_McpToolAudit_audit_date] DEFAULT (getdate()),
    [audit_login] varchar(128) NOT NULL CONSTRAINT [DF_McpToolAudit_audit_login] DEFAULT (original_login()),
    [mcptool_fk] varchar(32) NOT NULL,
    [title] nvarchar(80) NOT NULL,
    [description] nvarchar(1000) NOT NULL
);
END
GO
CREATE OR ALTER TRIGGER [dbo].[trMcpTool_Audit] ON [dbo].[McpTool]
AFTER INSERT, UPDATE, DELETE
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO [dbo].[McpToolAudit] ([audit_action], [mcptool_fk], [title], [description])
    SELECT CASE WHEN d.[tool_name] IS NULL THEN 'I' ELSE 'U' END, i.[tool_name], i.[title], i.[description]
    FROM inserted AS i LEFT JOIN deleted AS d ON i.[tool_name] = d.[tool_name];
    INSERT INTO [dbo].[McpToolAudit] ([audit_action], [mcptool_fk], [title], [description])
    SELECT 'D', d.[tool_name], d.[title], d.[description]
    FROM deleted AS d LEFT JOIN inserted AS i ON i.[tool_name] = d.[tool_name] WHERE i.[tool_name] IS NULL;
END
GO
CREATE OR ALTER VIEW [dbo].[vMcpServer] AS
SELECT [id], [title], [about]
FROM [dbo].[McpServer];
GO
CREATE OR ALTER VIEW [dbo].[vMcpTool] AS
SELECT [tool_name], [title], [description]
FROM [dbo].[McpTool];
GO
IF NOT EXISTS (SELECT 1 FROM [dbo].[McpServer] WHERE [id] = 1)
    INSERT INTO [dbo].[McpServer] ([id], [title], [about]) VALUES (1, N'DataGateway', N'Read-only access to cataloged data sources.');
GO
IF NOT EXISTS (SELECT 1 FROM [dbo].[McpTool] WHERE [tool_name] = 'list_sources')
    INSERT INTO [dbo].[McpTool] ([tool_name], [title], [description]) VALUES ('list_sources', N'List sources', N'No source named? See all data sources and what each holds. Takes no arguments and returns {"sources":[...]}: each active source''s name, title, description, grain (what one row represents) and rowCap (maximum rows per query). Pick the source whose description and grain fit the question, then call describe_source with its name.');
GO
IF NOT EXISTS (SELECT 1 FROM [dbo].[McpTool] WHERE [tool_name] = 'describe_source')
    INSERT INTO [dbo].[McpTool] ([tool_name], [title], [description]) VALUES ('describe_source', N'Describe a source', N'Named source not yet described? Get schema and query rules. Returns one source (name, title, description, grain, rowCap) with querySchema: every column with its meaning, type, unit, magnitude, display format and can* flags, then the rules of each query clause. Use only listed columns, as their flags allow. Before stating a number, read its unit and magnitude: "millions" means 523.06 is 523.06 million, for sum_, avg_, min_ and max_ outputs too; a count_ output is a plain count.');
GO
IF NOT EXISTS (SELECT 1 FROM [dbo].[McpTool] WHERE [tool_name] = 'query_source')
    INSERT INTO [dbo].[McpTool] ([tool_name], [title], [description]) VALUES ('query_source', N'Query a source', N'Source already described? Run a read-only query, get rows. Build query from that source''s querySchema (describe_source). Returns {source, columns, rows, rowCount, more, hint}. Prefer filters and aggregates over pulling raw rows. With or without limit, more is true exactly when rows were left out: never report that answer as complete. Narrow or aggregate; there is no way to ask for the rest. Empty rows, or an aggregate''s 0 or null, can mean no row matched or a misspelled value.');
GO
