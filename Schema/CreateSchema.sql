-- =============================================================================
-- Code-search vector index schema  (SQL Server 2025 on-prem / Azure SQL Database)
--
-- One row per source file (dbo.CodeFiles) + one row per semantic code chunk (dbo.CodeChunks).
-- Chunk embeddings are e5-large-v2 (1024-dim), stored in a native VECTOR column and served by a
-- DiskANN vector index; the text columns feed full-text search for the keyword half of hybrid search.
--
-- RUN ORDER:
--   1) Run THIS script  → creates both tables + full-text indexes + b-tree indexes.
--      It does NOT create the DiskANN vector index (that needs >= 100 rows of
--      non-NULL vectors and cannot be created on an empty table).
--   2) Run the indexer  → loads chunks, THEN creates the vector index:
--        CREATE VECTOR INDEX IX_CodeChunks_Embedding ON dbo.CodeChunks(Embedding)
--          WITH (METRIC='cosine', TYPE='diskann');
-- =============================================================================

-- DiskANN VECTOR INDEX requires this on on-prem SQL Server 2025 (persists at DB scope).
-- Azure SQL Database / Fabric don't need it; wrapped so it's harmless/no-op there.
BEGIN TRY
   ALTER DATABASE SCOPED CONFIGURATION SET PREVIEW_FEATURES = ON;
END TRY
BEGIN CATCH
   PRINT 'PREVIEW_FEATURES not applicable on this platform — continuing.';
END CATCH
GO

-- =============================================================================
-- TABLE 1: CodeFiles  (one row per indexed source file)
-- =============================================================================
IF OBJECT_ID('dbo.CodeChunks', 'U') IS NOT NULL DROP TABLE dbo.CodeChunks;
IF OBJECT_ID('dbo.CodeFiles', 'U') IS NOT NULL DROP TABLE dbo.CodeFiles;
GO

CREATE TABLE dbo.CodeFiles
(
   Id                    INT IDENTITY(1,1) NOT NULL,
   FilePath              NVARCHAR(500)     NOT NULL,
   Content               NVARCHAR(MAX)     NULL,
   ClassName             NVARCHAR(255)     NULL,
   PropertyNames         NVARCHAR(MAX)     NULL,
   MethodNames           NVARCHAR(MAX)     NULL,
   EnumValues            NVARCHAR(MAX)     NULL,
   FileType              NVARCHAR(50)      NULL,
   Namespace             NVARCHAR(500)     NULL,
   BaseClass             NVARCHAR(500)     NULL,
   Interfaces            NVARCHAR(500)     NULL,
   Usings                NVARCHAR(4000)    NULL,
   Regions               NVARCHAR(2000)    NULL,
   ClassNames            NVARCHAR(4000)    NULL,
   OverriddenMethods     NVARCHAR(2000)    NULL,
   AbstractMethods       NVARCHAR(2000)    NULL,
   VirtualMethods        NVARCHAR(2000)    NULL,
   AsyncMethods          NVARCHAR(MAX)     NULL,
   Attributes            NVARCHAR(2000)    NULL,
   Constants             NVARCHAR(4000)    NULL,
   StaticFields          NVARCHAR(2000)    NULL,
   SqlOperations         NVARCHAR(100)     NULL,
   Dictionaries          NVARCHAR(2000)    NULL,
   GenericTypes          NVARCHAR(MAX)     NULL,
   ClassModifiers        NVARCHAR(100)     NULL,
   Constructors          NVARCHAR(MAX)     NULL,
   Properties            NVARCHAR(MAX)     NULL,
   Events                NVARCHAR(2000)    NULL,
   Delegates             NVARCHAR(1000)    NULL,
   Author                NVARCHAR(100)     NULL,
   FileAddDate           NVARCHAR(MAX)     NULL,
   AllAuthors            NVARCHAR(1000)    NULL,
   CommitMessages        NVARCHAR(MAX)     NULL,
   WorkItemTitles        NVARCHAR(MAX)     NULL,
   WorkItemTags          NVARCHAR(MAX)     NULL,
   ModifiedDate          DATETIME2(7)      NOT NULL CONSTRAINT DF_CodeFiles_ModifiedDate DEFAULT (GETDATE()),
   ReferencedTypes       NVARCHAR(MAX)     NULL,

   CONSTRAINT PK_CodeFiles PRIMARY KEY CLUSTERED (Id),
   CONSTRAINT UQ_CodeFiles_FilePath UNIQUE (FilePath)
);
GO

-- =============================================================================
-- TABLE 2: CodeChunks  (method/class chunks; e5-large-v2 => 1024-dim)
-- =============================================================================
CREATE TABLE dbo.CodeChunks
(
   Id                    INT IDENTITY(1,1) NOT NULL,
   CodeFileId            INT               NOT NULL,
   ChunkKey              NVARCHAR(500)     NOT NULL,   -- stable path:type:name:startLine
   ChunkType             NVARCHAR(50)      NOT NULL,
   ChunkName             NVARCHAR(200)     NOT NULL,
   StartLine             INT               NOT NULL,
   EndLine               INT               NOT NULL,
   ChunkContent          NVARCHAR(MAX)     NOT NULL,
   Embedding             VECTOR(1024)      NULL,       -- nullable: rows can exist pre-embedding
   Namespace             NVARCHAR(500)     NULL,
   ClassName             NVARCHAR(200)     NULL,
   Signature             NVARCHAR(MAX)     NULL,
   ParentContext         NVARCHAR(MAX)     NULL,

   CONSTRAINT PK_CodeChunks PRIMARY KEY CLUSTERED (Id),
   CONSTRAINT FK_CodeChunks_CodeFiles
      FOREIGN KEY (CodeFileId) REFERENCES dbo.CodeFiles(Id) ON DELETE CASCADE,
   CONSTRAINT UQ_CodeChunks_ChunkKey UNIQUE (ChunkKey)
);
GO

-- B-tree indexes
CREATE NONCLUSTERED INDEX IX_CodeChunks_CodeFileId ON dbo.CodeChunks (CodeFileId);
CREATE NONCLUSTERED INDEX IX_CodeChunks_ChunkType  ON dbo.CodeChunks (ChunkType);
GO

-- =============================================================================
-- FULL-TEXT SEARCH (the keyword half of hybrid search)
-- =============================================================================
IF NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = 'CODEINDEX_FTC')
   CREATE FULLTEXT CATALOG CODEINDEX_FTC AS DEFAULT;
GO

CREATE FULLTEXT INDEX ON dbo.CodeFiles
(
   Content, ClassName, ClassNames, BaseClass, Namespace, Interfaces,
   PropertyNames, MethodNames, Properties, Constructors,
   OverriddenMethods, AbstractMethods, VirtualMethods, AsyncMethods,
   EnumValues, Attributes, Constants, GenericTypes, Usings,
   CommitMessages, WorkItemTitles, WorkItemTags, AllAuthors
)
KEY INDEX PK_CodeFiles ON CODEINDEX_FTC WITH (CHANGE_TRACKING AUTO);
GO

CREATE FULLTEXT INDEX ON dbo.CodeChunks
(
   ChunkContent, ChunkKey, ChunkName, ClassName, Namespace, Signature, ParentContext
)
KEY INDEX PK_CodeChunks ON CODEINDEX_FTC WITH (CHANGE_TRACKING AUTO);
GO

PRINT '=== Schema created: dbo.CodeFiles, dbo.CodeChunks + full-text indexes + b-tree indexes ===';
PRINT '=== Vector index is created by the indexer AFTER the first load (needs >=100 non-NULL vectors). ===';
GO
