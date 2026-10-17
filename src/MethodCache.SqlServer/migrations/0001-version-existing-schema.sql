-- Offline migration from the pre-1.0 unversioned MethodCache schema to schema contract 1.
-- Stop every MethodCache node first. Back up the database. Edit only these identifiers to match SqlServerOptions.
DECLARE @SchemaName sysname = N'dbo';
DECLARE @TableName sysname = N'MethodCache';
DECLARE @InvalidationsTableName sysname = N'MethodCacheInvalidations';

SET XACT_ABORT ON;
DECLARE @cache nvarchar(520) = QUOTENAME(@SchemaName) + N'.' + QUOTENAME(@TableName);
DECLARE @invalidations nvarchar(520) = QUOTENAME(@SchemaName) + N'.' + QUOTENAME(@InvalidationsTableName);
DECLARE @version nvarchar(520) = QUOTENAME(@SchemaName) + N'.' + QUOTENAME(@TableName + N'Schema');
IF OBJECT_ID(@cache, N'U') IS NULL OR OBJECT_ID(@invalidations, N'U') IS NULL
    THROW 50001, 'Both existing MethodCache tables are required. New installations use normal schema creation.', 1;
IF EXISTS (
    SELECT 1 FROM (VALUES
(N'Id', TYPE_ID('nvarchar'), 898, 0, 0),
(N'Value', TYPE_ID('varbinary'), -1, 0, 0),
(N'ExpiresAtTime', TYPE_ID('datetimeoffset'), 10, 0, 0),
(N'SlidingExpirationInSeconds', TYPE_ID('bigint'), 8, 1, 0),
(N'AbsoluteExpiration', TYPE_ID('datetimeoffset'), 10, 1, 0)
    ) AS expected([Name], [Type], [Length], [Nullable], [Identity])
    LEFT JOIN sys.columns actual ON actual.object_id = OBJECT_ID(@cache) AND actual.name = expected.[Name]
    WHERE actual.column_id IS NULL OR actual.system_type_id <> expected.[Type]
        OR actual.max_length <> expected.[Length] OR actual.is_nullable <> expected.[Nullable]
        OR actual.is_identity <> expected.[Identity])
    THROW 50001, 'MethodCache cache columns are incompatible. Apply a supported explicit migration.', 1;
IF EXISTS (
    SELECT 1 FROM (VALUES
(N'Id', TYPE_ID('bigint'), 8, 0, 1),
(N'Scope', TYPE_ID('binary'), 32, 0, 0),
(N'InstanceId', TYPE_ID('varchar'), 32, 0, 0),
(N'MessageType', TYPE_ID('tinyint'), 1, 0, 0),
(N'Target', TYPE_ID('nvarchar'), -1, 0, 0),
(N'CreatedAt', TYPE_ID('datetime2'), 8, 0, 0)
    ) AS expected([Name], [Type], [Length], [Nullable], [Identity])
    LEFT JOIN sys.columns actual ON actual.object_id = OBJECT_ID(@invalidations) AND actual.name = expected.[Name]
    WHERE actual.column_id IS NULL OR actual.system_type_id <> expected.[Type]
        OR actual.max_length <> expected.[Length] OR actual.is_nullable <> expected.[Nullable]
        OR actual.is_identity <> expected.[Identity])
    THROW 50001, 'MethodCache invalidations columns are incompatible. Apply a supported explicit migration.', 1;

BEGIN TRY
    BEGIN TRANSACTION;
    IF OBJECT_ID(@version, N'U') IS NULL
    BEGIN
        DECLARE @create nvarchar(max) = N'CREATE TABLE ' + @version
            + N' ([Id] tinyint NOT NULL PRIMARY KEY CHECK ([Id] = 1), [Version] int NOT NULL); INSERT INTO '
            + @version + N' ([Id], [Version]) VALUES (1, 1);';
        EXEC sys.sp_executesql @create;
    END
    ELSE
    BEGIN
        IF EXISTS (
            SELECT 1 FROM (VALUES
        (N'Id', TYPE_ID('tinyint'), 1, 0, 0),
        (N'Version', TYPE_ID('int'), 4, 0, 0)
            ) AS expected([Name], [Type], [Length], [Nullable], [Identity])
            LEFT JOIN sys.columns actual ON actual.object_id = OBJECT_ID(@version) AND actual.name = expected.[Name]
            WHERE actual.column_id IS NULL OR actual.system_type_id <> expected.[Type]
                OR actual.max_length <> expected.[Length] OR actual.is_nullable <> expected.[Nullable]
                OR actual.is_identity <> expected.[Identity])
            THROW 50001, 'MethodCache schema-version columns are incompatible. Apply a supported explicit migration.', 1;
        DECLARE @verify nvarchar(max) = N'IF (SELECT COUNT(*) FROM ' + @version
            + N') <> 1 OR NOT EXISTS (SELECT 1 FROM ' + @version
            + N' WHERE [Id] = 1 AND [Version] = 1) THROW 50001, ''The schema version is incompatible.'', 1;';
        EXEC sys.sp_executesql @verify;
    END
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(@invalidations) AND name = 'Index_Scope_Id')
        EXEC(N'CREATE NONCLUSTERED INDEX [Index_Scope_Id] ON ' + @invalidations + N' ([Scope], [Id]);');
    IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID(@invalidations) AND name = 'Index_Scope_CreatedAt')
        EXEC(N'CREATE NONCLUSTERED INDEX [Index_Scope_CreatedAt] ON ' + @invalidations + N' ([Scope], [CreatedAt]);');
    -- All nodes are stopped; old pre-1.0 frames must not be replayed as contract-1 invalidations.
    DECLARE @clear nvarchar(max) = N'DELETE FROM ' + @invalidations + N';';
    EXEC sys.sp_executesql @clear;
    COMMIT TRANSACTION;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0 ROLLBACK TRANSACTION;
    THROW;
END CATCH;
