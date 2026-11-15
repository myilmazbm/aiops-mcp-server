-- Server inventory snapshots: one row per server, upserted, no history.
-- The database itself must be created with SQL_Latin1_General_CP1_CI_AS collation
-- (see deploy/sql/CreateDatabase.sql); string comparisons rely on it.
IF NOT EXISTS (SELECT 1 FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id
               WHERE s.name = 'dbo' AND t.name = 'ServerInventory')
BEGIN
    CREATE TABLE dbo.ServerInventory
    (
        ServerName     nvarchar(255)  NOT NULL CONSTRAINT PK_ServerInventory PRIMARY KEY,
        CollectedAtUtc datetime2(0)   NOT NULL,
        Services       nvarchar(max)  NULL,
        Software       nvarchar(max)  NULL,
        Ports          nvarchar(max)  NULL,
        Gpo            nvarchar(max)  NULL,
        Hotfix         nvarchar(max)  NULL,
        Certs          nvarchar(max)  NULL,
        Tasks          nvarchar(max)  NULL
    );
END
