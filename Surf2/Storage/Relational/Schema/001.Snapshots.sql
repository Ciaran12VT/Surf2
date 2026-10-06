-- Run after Core, before Capture. Provisioning is owned by the migration runner.
-- Names/legacy IDs are never SQL identifiers or unique keys. Hashes are UTF-16LE
-- SHA-256 accelerators, not identity/equality proofs.
CREATE TABLE surf.SnapshotCatalogueHead (
    UserKey bigint PRIMARY KEY REFERENCES surf.UserProfile(ProfileKey),
    RowVersion rowversion NOT NULL
);
INSERT surf.SnapshotCatalogueHead(UserKey) VALUES(1);

CREATE TABLE surf.DatabaseSnapshot (
    SnapshotKey bigint IDENTITY PRIMARY KEY,
    UserKey bigint NOT NULL REFERENCES surf.UserProfile(ProfileKey),
    PublicId uniqueidentifier NOT NULL DEFAULT NEWID(),
    OriginalSnapshotId nvarchar(max) NOT NULL,
    SnapshotIdHash binary(32) NOT NULL,
    DisplayName nvarchar(max) NOT NULL,
    DatabaseName nvarchar(max) NOT NULL,
    ImportedAtUtc datetimeoffset(7) NOT NULL,
    SortOrdinal bigint NOT NULL CHECK (SortOrdinal >= 0),
    CurrentVersionKey bigint NULL,
    IsPublished bit NOT NULL DEFAULT 0,
    RowVersion rowversion NOT NULL,
    CONSTRAINT UQ_DatabaseSnapshot_PublicId UNIQUE (PublicId)
);
CREATE INDEX IX_DatabaseSnapshot_Order ON surf.DatabaseSnapshot(UserKey, SortOrdinal, SnapshotKey);
CREATE INDEX IX_DatabaseSnapshot_Legacy ON surf.DatabaseSnapshot(UserKey, SnapshotIdHash);

CREATE TABLE surf.SnapshotResource (
    ResourceKey bigint IDENTITY PRIMARY KEY,
    SnapshotKey bigint NOT NULL REFERENCES surf.DatabaseSnapshot(SnapshotKey),
    PublicId uniqueidentifier NOT NULL DEFAULT NEWID(),
    -- NULL is a preserved SqlDatabaseObjectKind.Unknown, not a new history enum.
    Kind int NULL CHECK (Kind BETWEEN 0 AND 5),
    SchemaName nvarchar(max) NOT NULL,
    ObjectName nvarchar(max) NOT NULL,
    NameHash binary(32) NOT NULL,
    OriginalResourceKey nvarchar(max) NOT NULL,
    ResourceKeyHash binary(32) NOT NULL,
    CurrentRevisionKey bigint NULL,
    CurrentSortOrdinal bigint NOT NULL CHECK (CurrentSortOrdinal >= 0),
    RowVersion rowversion NOT NULL,
    CONSTRAINT UQ_SnapshotResource_Owner UNIQUE (ResourceKey, SnapshotKey),
    CONSTRAINT UQ_SnapshotResource_PublicId UNIQUE (PublicId)
);
CREATE INDEX IX_SnapshotResource_Order ON surf.SnapshotResource(SnapshotKey, Kind, CurrentSortOrdinal, ResourceKey);
CREATE INDEX IX_SnapshotResource_Name ON surf.SnapshotResource(SnapshotKey, Kind, NameHash);
CREATE INDEX IX_SnapshotResource_Legacy ON surf.SnapshotResource(SnapshotKey, ResourceKeyHash);

-- Capture owns surf/capture dataset catalogues and references this exact PK.
CREATE TABLE surf.SnapshotResourceRevision (
    RevisionKey bigint IDENTITY PRIMARY KEY,
    ResourceKey bigint NOT NULL,
    SnapshotKey bigint NOT NULL,
    IsSealed bit NOT NULL DEFAULT 0,
    FOREIGN KEY (ResourceKey, SnapshotKey) REFERENCES surf.SnapshotResource(ResourceKey, SnapshotKey),
    CONSTRAINT UQ_SnapshotResourceRevision_Snapshot UNIQUE (RevisionKey, SnapshotKey),
    CONSTRAINT UQ_SnapshotResourceRevision_Owner UNIQUE (ResourceKey, RevisionKey)
);
ALTER TABLE surf.SnapshotResource ADD CONSTRAINT FK_SnapshotResource_Current
    FOREIGN KEY (ResourceKey, CurrentRevisionKey)
    REFERENCES surf.SnapshotResourceRevision(ResourceKey, RevisionKey);

CREATE TABLE surf.DatabaseObjectRevision (
    RevisionKey bigint PRIMARY KEY REFERENCES surf.SnapshotResourceRevision(RevisionKey),
    SchemaName nvarchar(max) NOT NULL,
    ObjectName nvarchar(max) NOT NULL,
    ObjectKind int NOT NULL CHECK (ObjectKind BETWEEN 0 AND 4),
    TypeDescription nvarchar(max) NOT NULL,
    ParentSchemaName nvarchar(max) NOT NULL,
    ParentObjectName nvarchar(max) NOT NULL,
    DefinitionContentKey bigint NOT NULL REFERENCES surf.TextContent(ContentKey)
);
CREATE TABLE surf.TableMetadataRevision (
    RevisionKey bigint PRIMARY KEY REFERENCES surf.SnapshotResourceRevision(RevisionKey),
    SnapshotKey bigint NOT NULL,
    SchemaName nvarchar(max) NOT NULL,
    TableName nvarchar(max) NOT NULL,
    HasFullData bit NOT NULL,
    FullDataRowCount bigint NOT NULL,
    FullDataImportedAtUtc datetimeoffset(7) NULL,
    FOREIGN KEY (RevisionKey, SnapshotKey) REFERENCES surf.SnapshotResourceRevision(RevisionKey, SnapshotKey),
    CONSTRAINT UQ_TableMetadataRevision_Snapshot UNIQUE (RevisionKey, SnapshotKey)
);
CREATE TABLE surf.TableDataRevision (
    RevisionKey bigint PRIMARY KEY REFERENCES surf.SnapshotResourceRevision(RevisionKey),
    SchemaName nvarchar(max) NOT NULL,
    TableName nvarchar(max) NOT NULL
);
-- A TableData PreviousPayload can contain its own SqlTable, independently of
-- the metadata revision. Absence of this row preserves a null Payload.Table.
CREATE TABLE surf.TableDataRevisionMetadata (
    RevisionKey bigint PRIMARY KEY REFERENCES surf.SnapshotResourceRevision(RevisionKey),
    SchemaName nvarchar(max) NOT NULL,
    TableName nvarchar(max) NOT NULL,
    HasFullData bit NOT NULL,
    FullDataRowCount bigint NOT NULL,
    FullDataImportedAtUtc datetimeoffset(7) NULL
);
CREATE TABLE surf.TableColumnRevision (
    ColumnRevisionKey bigint IDENTITY PRIMARY KEY,
    SnapshotKey bigint NOT NULL REFERENCES surf.DatabaseSnapshot(SnapshotKey),
    TableMetadataRevisionKey bigint NULL,
    SchemaName nvarchar(max) NOT NULL,
    TableName nvarchar(max) NOT NULL,
    ColumnName nvarchar(max) NOT NULL,
    DataType nvarchar(max) NOT NULL,
    MaxLength int NOT NULL,
    NumericPrecision tinyint NOT NULL,
    NumericScale int NOT NULL,
    IsNullable bit NOT NULL,
    IsIdentity bit NOT NULL,
    SourceOrdinal int NOT NULL,
    SortOrdinal bigint NOT NULL CHECK (SortOrdinal >= 0),
    CONSTRAINT UQ_TableColumnRevision_Owner UNIQUE (SnapshotKey, ColumnRevisionKey),
    FOREIGN KEY (TableMetadataRevisionKey, SnapshotKey) REFERENCES surf.TableMetadataRevision(RevisionKey, SnapshotKey)
);
CREATE INDEX IX_TableColumnRevision_Order ON surf.TableColumnRevision(TableMetadataRevisionKey, SortOrdinal, ColumnRevisionKey);
CREATE TABLE surf.PrimaryKeyConstraint (
    ConstraintKey bigint IDENTITY PRIMARY KEY,
    SnapshotKey bigint NOT NULL REFERENCES surf.DatabaseSnapshot(SnapshotKey),
    TableMetadataRevisionKey bigint NULL,
    ConstraintName nvarchar(max) NOT NULL,
    ConstraintNameHash binary(32) NOT NULL,
    CONSTRAINT UQ_PrimaryKeyConstraint_Owner UNIQUE (SnapshotKey, ConstraintKey),
    CONSTRAINT UQ_PrimaryKeyConstraint_Table UNIQUE (SnapshotKey, TableMetadataRevisionKey, ConstraintKey),
    FOREIGN KEY (TableMetadataRevisionKey, SnapshotKey) REFERENCES surf.TableMetadataRevision(RevisionKey, SnapshotKey)
);
CREATE INDEX IX_PrimaryKeyConstraint_Name ON surf.PrimaryKeyConstraint(TableMetadataRevisionKey, ConstraintNameHash);
CREATE TABLE surf.PrimaryKeyColumn (
    KeyColumnKey bigint IDENTITY PRIMARY KEY,
    SnapshotKey bigint NOT NULL REFERENCES surf.DatabaseSnapshot(SnapshotKey),
    TableMetadataRevisionKey bigint NULL,
    ConstraintKey bigint NOT NULL,
    SchemaName nvarchar(max) NOT NULL,
    TableName nvarchar(max) NOT NULL,
    ConstraintName nvarchar(max) NOT NULL,
    ColumnName nvarchar(max) NOT NULL,
    KeyOrdinal int NOT NULL,
    SortOrdinal bigint NOT NULL CHECK (SortOrdinal >= 0),
    CONSTRAINT FK_PrimaryKeyColumn_Constraint FOREIGN KEY (SnapshotKey, ConstraintKey)
        REFERENCES surf.PrimaryKeyConstraint(SnapshotKey, ConstraintKey),
    FOREIGN KEY (TableMetadataRevisionKey, SnapshotKey) REFERENCES surf.TableMetadataRevision(RevisionKey, SnapshotKey),
    FOREIGN KEY (SnapshotKey, TableMetadataRevisionKey, ConstraintKey)
        REFERENCES surf.PrimaryKeyConstraint(SnapshotKey, TableMetadataRevisionKey, ConstraintKey),
    CONSTRAINT UQ_PrimaryKeyColumn_Owner UNIQUE (SnapshotKey, KeyColumnKey)
);
CREATE INDEX IX_PrimaryKeyColumn_Order ON surf.PrimaryKeyColumn(TableMetadataRevisionKey, SortOrdinal, KeyColumnKey);

-- These relations preserve the ORIGINAL snapshot-wide collections, including
-- orphan entries and duplicates. Revision-local order is separately retained.
CREATE TABLE surf.SnapshotCurrentColumn (
    EntryKey bigint IDENTITY PRIMARY KEY,
    SnapshotKey bigint NOT NULL,
    ColumnRevisionKey bigint NOT NULL,
    SortOrdinal bigint NOT NULL CHECK (SortOrdinal >= 0),
    FOREIGN KEY (SnapshotKey, ColumnRevisionKey) REFERENCES surf.TableColumnRevision(SnapshotKey, ColumnRevisionKey)
);
CREATE INDEX IX_SnapshotCurrentColumn_Order ON surf.SnapshotCurrentColumn(SnapshotKey, SortOrdinal, EntryKey);
CREATE TABLE surf.SnapshotCurrentPrimaryKey (
    EntryKey bigint IDENTITY PRIMARY KEY,
    SnapshotKey bigint NOT NULL,
    KeyColumnKey bigint NOT NULL,
    SortOrdinal bigint NOT NULL CHECK (SortOrdinal >= 0),
    FOREIGN KEY (SnapshotKey, KeyColumnKey) REFERENCES surf.PrimaryKeyColumn(SnapshotKey, KeyColumnKey)
);
CREATE INDEX IX_SnapshotCurrentPrimaryKey_Order ON surf.SnapshotCurrentPrimaryKey(SnapshotKey, SortOrdinal, EntryKey);
CREATE TABLE surf.FullDataTableSelection (
    SelectionKey bigint IDENTITY PRIMARY KEY,
    SnapshotKey bigint NOT NULL REFERENCES surf.DatabaseSnapshot(SnapshotKey),
    OriginalTableName nvarchar(max) NOT NULL,
    SortOrdinal bigint NOT NULL CHECK (SortOrdinal >= 0)
);
CREATE INDEX IX_FullDataTableSelection_Order ON surf.FullDataTableSelection(SnapshotKey, SortOrdinal, SelectionKey);

CREATE TABLE surf.SnapshotHistory (
    HistoryKey bigint IDENTITY PRIMARY KEY,
    SnapshotKey bigint NOT NULL REFERENCES surf.DatabaseSnapshot(SnapshotKey),
    OriginalSnapshotId nvarchar(max) NOT NULL,
    NextVersionNumber int NOT NULL,
    SortOrdinal bigint NOT NULL CHECK (SortOrdinal >= 0),
    RowVersion rowversion NOT NULL,
    CONSTRAINT UQ_SnapshotHistory_Owner UNIQUE (HistoryKey, SnapshotKey)
);
CREATE INDEX IX_SnapshotHistory_Order ON surf.SnapshotHistory(SnapshotKey, SortOrdinal, HistoryKey);
CREATE TABLE surf.SnapshotVersion (
    VersionKey bigint IDENTITY PRIMARY KEY,
    HistoryKey bigint NOT NULL,
    SnapshotKey bigint NOT NULL,
    OriginalVersionId nvarchar(max) NOT NULL,
    VersionIdHash binary(32) NOT NULL,
    VersionName nvarchar(max) NOT NULL,
    VersionNumber int NOT NULL,
    CreatedAtUtc datetimeoffset(7) NOT NULL,
    IsInitial bit NOT NULL,
    SortOrdinal bigint NOT NULL CHECK (SortOrdinal >= 0),
    FOREIGN KEY (HistoryKey, SnapshotKey) REFERENCES surf.SnapshotHistory(HistoryKey, SnapshotKey),
    CONSTRAINT UQ_SnapshotVersion_Owner UNIQUE (VersionKey, SnapshotKey),
    CONSTRAINT UQ_SnapshotVersion_ChangeOwner UNIQUE (VersionKey, SnapshotKey, VersionNumber)
);
CREATE INDEX IX_SnapshotVersion_Order ON surf.SnapshotVersion(HistoryKey, SortOrdinal, VersionKey);
CREATE INDEX IX_SnapshotVersion_Reverse ON surf.SnapshotVersion(HistoryKey, VersionNumber DESC, SortOrdinal, VersionKey);
CREATE INDEX IX_SnapshotVersion_Legacy ON surf.SnapshotVersion(HistoryKey, VersionIdHash, SortOrdinal, VersionKey);
ALTER TABLE surf.DatabaseSnapshot ADD CONSTRAINT FK_DatabaseSnapshot_Version
    FOREIGN KEY (CurrentVersionKey, SnapshotKey) REFERENCES surf.SnapshotVersion(VersionKey, SnapshotKey);
CREATE TABLE surf.SnapshotChange (
    ChangeKey bigint IDENTITY PRIMARY KEY,
    VersionKey bigint NOT NULL,
    SnapshotKey bigint NOT NULL,
    VersionNumber int NOT NULL,
    ResourceKey bigint NOT NULL,
    Kind int NOT NULL CHECK (Kind BETWEEN 0 AND 5),
    ChangeKind int NOT NULL CHECK (ChangeKind BETWEEN 0 AND 2),
    OriginalResourceKey nvarchar(max) NOT NULL,
    DisplayName nvarchar(max) NOT NULL,
    RelativePath nvarchar(max) NOT NULL,
    SortOrdinal bigint NOT NULL CHECK (SortOrdinal >= 0),
    PreviousRevisionKey bigint NULL,
    FOREIGN KEY (VersionKey, SnapshotKey, VersionNumber) REFERENCES surf.SnapshotVersion(VersionKey, SnapshotKey, VersionNumber),
    FOREIGN KEY (ResourceKey, SnapshotKey) REFERENCES surf.SnapshotResource(ResourceKey, SnapshotKey),
    FOREIGN KEY (ResourceKey, PreviousRevisionKey) REFERENCES surf.SnapshotResourceRevision(ResourceKey, RevisionKey),
    CHECK (ChangeKind <> 0 OR PreviousRevisionKey IS NULL)
);
CREATE INDEX IX_SnapshotChange_Order ON surf.SnapshotChange(VersionKey, SortOrdinal, ChangeKey);
CREATE INDEX IX_SnapshotChange_Reverse ON surf.SnapshotChange(ResourceKey, VersionNumber, SortOrdinal DESC, ChangeKey DESC)
    INCLUDE (ChangeKind, PreviousRevisionKey);
