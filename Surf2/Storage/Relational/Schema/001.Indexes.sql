-- Derived storage only. Run after Core, Snapshots and State. No runtime rollout.
-- Enum values below are shared with IndexContracts.cs; no arbitrary target key.
CREATE TYPE surf.IndexKeyBatch AS TABLE (Id bigint NOT NULL PRIMARY KEY);
CREATE TYPE surf.IndexNameBatch AS TABLE (
    Ordinal int NOT NULL PRIMARY KEY, NameHash binary(32) NOT NULL, IsAscii bit NOT NULL);
CREATE TYPE surf.IndexSymbolBatch AS TABLE (
    Ordinal int NOT NULL PRIMARY KEY,
    Name nvarchar(max) NOT NULL, QualifiedName nvarchar(max) NOT NULL, Kind int NOT NULL,
    Locator nvarchar(max) NOT NULL, LineNumber int NOT NULL, ColumnNumber int NOT NULL,
    EndLineNumber int NOT NULL, EndColumnNumber int NOT NULL,
    ParameterCount int NULL, MinimumArgumentCount int NULL, MaximumArgumentCount int NULL,
    Language nvarchar(128) NOT NULL, ContainerName nvarchar(max) NOT NULL,
    NormalizedName nvarchar(max) NOT NULL, NameHash binary(32) NOT NULL, IsAscii bit NOT NULL,
    NormalizedQualifiedName nvarchar(max) NOT NULL, QualifiedHash binary(32) NOT NULL, QualifiedIsAscii bit NOT NULL);

CREATE TABLE surf.IndexCatalogueHead (
    Singleton tinyint NOT NULL PRIMARY KEY CHECK (Singleton = 1),
    Generation bigint NOT NULL DEFAULT 0 CHECK (Generation >= 0));
INSERT surf.IndexCatalogueHead(Singleton) VALUES (1);

CREATE TABLE surf.FileSource (
    FileSourceKey bigint IDENTITY NOT NULL PRIMARY KEY,
    OriginalPath nvarchar(max) NOT NULL,
    PathHash binary(32) NOT NULL,
    PathIsAscii bit NOT NULL,
    EncodingName nvarchar(128) NULL,
    LastSuccessfulFingerprint binary(32) NULL,
    LastSuccessfulByteCount bigint NULL CHECK (LastSuccessfulByteCount >= 0),
    LastSuccessfulWriteUtc datetimeoffset(7) NULL,
    LastVerifiedAtUtc datetimeoffset(7) NULL);
CREATE INDEX IX_FileSource_Path ON surf.FileSource(PathHash, FileSourceKey);

CREATE TABLE surf.Document (
    DocumentKey bigint IDENTITY NOT NULL PRIMARY KEY,
    PublicIdentity uniqueidentifier NOT NULL DEFAULT NEWID() UNIQUE,
    Kind int NOT NULL CHECK (Kind BETWEEN 0 AND 3), -- File, SQL definition, table code, diagram projection
    FileSourceKey bigint NULL REFERENCES surf.FileSource(FileSourceKey),
    SnapshotResourceKey bigint NULL,
    SnapshotKey bigint NULL,
    DiagramRevisionKey bigint NULL REFERENCES surf.DiagramRevision(DiagramRevisionKey),
    DisplayName nvarchar(max) NOT NULL,
    Language nvarchar(128) NOT NULL,
    CurrentRevisionKey bigint NULL,
    Freshness int NOT NULL DEFAULT 0 CHECK (Freshness BETWEEN 0 AND 5), -- Unindexed, Indexed, Stale, Missing, Inaccessible, Failed
    Version rowversion NOT NULL,
    FOREIGN KEY (SnapshotResourceKey, SnapshotKey) REFERENCES surf.SnapshotResource(ResourceKey, SnapshotKey),
    UNIQUE (DocumentKey, FileSourceKey),
    UNIQUE (DocumentKey, SnapshotResourceKey),
    UNIQUE (DocumentKey, DiagramRevisionKey),
    CHECK (Freshness <> 1 OR CurrentRevisionKey IS NOT NULL),
    CHECK ((Kind = 0 AND FileSourceKey IS NOT NULL AND SnapshotResourceKey IS NULL AND SnapshotKey IS NULL AND DiagramRevisionKey IS NULL)
        OR (Kind IN (1,2) AND FileSourceKey IS NULL AND SnapshotResourceKey IS NOT NULL AND SnapshotKey IS NOT NULL AND DiagramRevisionKey IS NULL)
        OR (Kind = 3 AND FileSourceKey IS NULL AND SnapshotResourceKey IS NULL AND SnapshotKey IS NULL AND DiagramRevisionKey IS NOT NULL)));
CREATE UNIQUE INDEX UX_Document_File ON surf.Document(FileSourceKey) WHERE FileSourceKey IS NOT NULL;
CREATE UNIQUE INDEX UX_Document_Snapshot ON surf.Document(SnapshotResourceKey, Kind) WHERE SnapshotResourceKey IS NOT NULL;
CREATE UNIQUE INDEX UX_Document_Diagram ON surf.Document(DiagramRevisionKey) WHERE DiagramRevisionKey IS NOT NULL;

CREATE TABLE surf.DocumentRevision (
    DocumentRevisionKey bigint IDENTITY NOT NULL PRIMARY KEY,
    DocumentKey bigint NOT NULL REFERENCES surf.Document(DocumentKey),
    PublicationId uniqueidentifier NOT NULL UNIQUE,
    ContentKey bigint NOT NULL REFERENCES surf.TextContent(ContentKey),
    FileSourceKey bigint NULL,
    SnapshotResourceKey bigint NULL,
    SourceRevisionKey bigint NULL,
    DiagramRevisionKey bigint NULL,
    SourceFingerprint binary(32) NOT NULL,
    SourceByteCount bigint NULL CHECK (SourceByteCount >= 0),
    SourceWriteUtc datetimeoffset(7) NULL,
    ParserVersion nvarchar(128) NOT NULL,
    RendererVersion nvarchar(128) NOT NULL,
    PolicyVersion nvarchar(128) NOT NULL,
    Language nvarchar(128) NOT NULL,
    PublishedAtUtc datetimeoffset(7) NOT NULL DEFAULT SYSDATETIMEOFFSET(),
    FOREIGN KEY (DocumentKey, FileSourceKey) REFERENCES surf.Document(DocumentKey, FileSourceKey),
    FOREIGN KEY (DocumentKey, SnapshotResourceKey) REFERENCES surf.Document(DocumentKey, SnapshotResourceKey),
    FOREIGN KEY (SnapshotResourceKey, SourceRevisionKey) REFERENCES surf.SnapshotResourceRevision(ResourceKey, RevisionKey),
    FOREIGN KEY (DocumentKey, DiagramRevisionKey) REFERENCES surf.Document(DocumentKey, DiagramRevisionKey),
    UNIQUE (DocumentKey, DocumentRevisionKey),
    UNIQUE (DocumentKey, DocumentRevisionKey, ContentKey),
    CHECK ((FileSourceKey IS NOT NULL AND SnapshotResourceKey IS NULL AND SourceRevisionKey IS NULL AND DiagramRevisionKey IS NULL AND SourceByteCount IS NOT NULL AND SourceWriteUtc IS NOT NULL)
        OR (FileSourceKey IS NULL AND SnapshotResourceKey IS NOT NULL AND SourceRevisionKey IS NOT NULL AND DiagramRevisionKey IS NULL AND SourceByteCount IS NULL AND SourceWriteUtc IS NULL)
        OR (FileSourceKey IS NULL AND SnapshotResourceKey IS NULL AND SourceRevisionKey IS NULL AND DiagramRevisionKey IS NOT NULL AND SourceByteCount IS NULL AND SourceWriteUtc IS NULL)));
ALTER TABLE surf.Document ADD CONSTRAINT FK_IndexDocument_Current FOREIGN KEY (DocumentKey, CurrentRevisionKey)
    REFERENCES surf.DocumentRevision(DocumentKey, DocumentRevisionKey);
CREATE INDEX IX_DocumentRevision_Order ON surf.DocumentRevision(DocumentKey, DocumentRevisionKey);

CREATE TABLE surf.DocumentLocator (
    LocatorKey bigint IDENTITY NOT NULL PRIMARY KEY,
    DocumentKey bigint NOT NULL REFERENCES surf.Document(DocumentKey),
    ScopeResourceKey bigint NOT NULL REFERENCES surf.ScopeResource(ScopeResourceKey),
    Kind int NOT NULL CHECK (Kind BETWEEN 0 AND 2), -- physical, readable db, canonical db (diagram uses readable)
    OriginalLocator nvarchar(max) NOT NULL,
    LocatorHash binary(32) NOT NULL,
    IsAscii bit NOT NULL);
CREATE INDEX IX_DocumentLocator_Context ON surf.DocumentLocator(ScopeResourceKey, LocatorHash, LocatorKey);

CREATE TABLE surf.ResourceDocument (
    ScopeResourceKey bigint NOT NULL REFERENCES surf.ScopeResource(ScopeResourceKey) ON DELETE CASCADE,
    DocumentKey bigint NOT NULL REFERENCES surf.Document(DocumentKey),
    DisplayName nvarchar(max) NOT NULL,
    Locator nvarchar(max) NOT NULL,
    NodeKey nvarchar(max) NOT NULL,
    ParentNodeKey nvarchar(max) NOT NULL,
    SortOrdinal bigint NOT NULL CHECK (SortOrdinal >= 0),
    PRIMARY KEY (ScopeResourceKey, DocumentKey));
CREATE INDEX IX_ResourceDocument_Reverse ON surf.ResourceDocument(DocumentKey, ScopeResourceKey);
ALTER TABLE surf.DocumentLocator ADD CONSTRAINT FK_IndexLocator_Membership
    FOREIGN KEY (ScopeResourceKey, DocumentKey) REFERENCES surf.ResourceDocument(ScopeResourceKey, DocumentKey) ON DELETE CASCADE;

CREATE TABLE surf.ScopeIndexState (
    ScopeKey bigint NOT NULL PRIMARY KEY REFERENCES surf.Scope(ScopeKey) ON DELETE CASCADE,
    ReconciledScopeVersion binary(8) NULL,
    ReconciledSnapshotVersion binary(8) NULL,
    ReconciledDiagramVersion binary(8) NULL,
    ReconciledAtUtc datetimeoffset(7) NULL);

CREATE TABLE surf.SymbolDefinition (
    SymbolKey bigint IDENTITY NOT NULL PRIMARY KEY,
    DocumentKey bigint NOT NULL,
    DocumentRevisionKey bigint NOT NULL,
    SortOrdinal int NOT NULL CHECK (SortOrdinal >= 0),
    Name nvarchar(max) NOT NULL,
    QualifiedName nvarchar(max) NOT NULL,
    Kind int NOT NULL CHECK (Kind BETWEEN 0 AND 12), -- ReferenceEntityKind
    Locator nvarchar(max) NOT NULL,
    LineNumber int NOT NULL CHECK (LineNumber >= 1),
    ColumnNumber int NOT NULL CHECK (ColumnNumber >= 1),
    EndLineNumber int NOT NULL,
    EndColumnNumber int NOT NULL CHECK (EndColumnNumber >= 1),
    ParameterCount int NULL CHECK (ParameterCount >= 0),
    MinimumArgumentCount int NULL CHECK (MinimumArgumentCount >= 0),
    MaximumArgumentCount int NULL CHECK (MaximumArgumentCount >= 0),
    Language nvarchar(128) NOT NULL,
    ContainerName nvarchar(max) NOT NULL,
    FOREIGN KEY (DocumentKey, DocumentRevisionKey) REFERENCES surf.DocumentRevision(DocumentKey, DocumentRevisionKey),
    UNIQUE (DocumentRevisionKey, SortOrdinal),
    CHECK (EndLineNumber >= LineNumber),
    CHECK (MinimumArgumentCount IS NULL OR MaximumArgumentCount IS NULL OR MinimumArgumentCount <= MaximumArgumentCount),
    CHECK (EndLineNumber <> LineNumber OR EndColumnNumber >= ColumnNumber));
CREATE INDEX IX_SymbolDefinition_Revision ON surf.SymbolDefinition(DocumentRevisionKey, SymbolKey);
CREATE TABLE surf.SymbolNameLookup (
    SymbolKey bigint NOT NULL REFERENCES surf.SymbolDefinition(SymbolKey),
    NameOrdinal tinyint NOT NULL CHECK (NameOrdinal IN (0,1)),
    NormalizedName nvarchar(max) NOT NULL,
    NameHash binary(32) NOT NULL,
    IsAscii bit NOT NULL,
    PRIMARY KEY (SymbolKey, NameOrdinal));
CREATE INDEX IX_SymbolNameLookup_Hash ON surf.SymbolNameLookup(NameHash, IsAscii, SymbolKey);

CREATE TABLE surf.SearchProjection (
    DocumentKey bigint NOT NULL,
    DocumentRevisionKey bigint NOT NULL PRIMARY KEY,
    ContentKey bigint NOT NULL REFERENCES surf.TextContent(ContentKey),
    ProjectionKind int NOT NULL CHECK (ProjectionKind IN (0,1,2)), -- raw file, raw definition, type/label/image diagram
    FOREIGN KEY (DocumentKey, DocumentRevisionKey, ContentKey) REFERENCES surf.DocumentRevision(DocumentKey, DocumentRevisionKey, ContentKey));
CREATE INDEX IX_SearchProjection_Document ON surf.SearchProjection(DocumentKey, DocumentRevisionKey);
-- Table metadata/data search deliberately belongs to the Capture provider.

CREATE TABLE surf.IndexWorkItem (
    WorkItemKey bigint IDENTITY NOT NULL PRIMARY KEY,
    DocumentKey bigint NOT NULL REFERENCES surf.Document(DocumentKey),
    PublicationId uniqueidentifier NOT NULL UNIQUE,
    ExpectedDocumentVersion binary(8) NOT NULL,
    SourceFingerprint binary(32) NOT NULL,
    ParserVersion nvarchar(128) NOT NULL,
    RendererVersion nvarchar(128) NOT NULL,
    PolicyVersion nvarchar(128) NOT NULL,
    State int NOT NULL CHECK (State BETWEEN 0 AND 3), -- pending, running, succeeded, failed
    LeaseId uniqueidentifier NULL,
    LeaseUntilUtc datetimeoffset(7) NULL,
    ErrorCode varchar(64) NULL, -- redacted code only; never source text/exception messages
    CreatedAtUtc datetimeoffset(7) NOT NULL DEFAULT SYSDATETIMEOFFSET(),
    FinishedAtUtc datetimeoffset(7) NULL,
    PublishedRevisionKey bigint NULL,
    FOREIGN KEY (DocumentKey, PublishedRevisionKey) REFERENCES surf.DocumentRevision(DocumentKey, DocumentRevisionKey),
    CHECK ((State = 2 AND PublishedRevisionKey IS NOT NULL AND FinishedAtUtc IS NOT NULL)
        OR (State <> 2 AND PublishedRevisionKey IS NULL)),
    CHECK (State <> 1 OR (LeaseId IS NOT NULL AND LeaseUntilUtc IS NOT NULL)));
CREATE INDEX IX_IndexWorkItem_Queue ON surf.IndexWorkItem(State, WorkItemKey) INCLUDE (DocumentKey, LeaseUntilUtc);
