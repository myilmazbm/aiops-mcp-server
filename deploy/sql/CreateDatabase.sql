-- One-time database bootstrap, run by a DBA. Table migrations are applied by the
-- application itself via DbUp (src/AiOps.Mcp/Db/Scripts).
--
-- Collation is pinned explicitly: the instance default on Turkish servers breaks
-- 'I/ı' case-insensitive comparisons of server and service names.
IF DB_ID(N'AiOpsInventory') IS NULL
BEGIN
    CREATE DATABASE AiOpsInventory
    COLLATE SQL_Latin1_General_CP1_CI_AS;
END
GO

-- The MCP service connects with its gMSA via integrated security; no SQL logins.
-- Example grant (adjust the account name):
--   CREATE LOGIN [CONTOSO\gMSA-AIOps$] FROM WINDOWS;
--   USE AiOpsInventory;
--   CREATE USER [CONTOSO\gMSA-AIOps$] FOR LOGIN [CONTOSO\gMSA-AIOps$];
--   ALTER ROLE db_datareader ADD MEMBER [CONTOSO\gMSA-AIOps$];
--   ALTER ROLE db_datawriter ADD MEMBER [CONTOSO\gMSA-AIOps$];
--   ALTER ROLE db_ddladmin  ADD MEMBER [CONTOSO\gMSA-AIOps$]; -- for DbUp migrations
