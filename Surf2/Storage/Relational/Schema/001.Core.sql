EXEC(N'CREATE SCHEMA surf');
EXEC(N'CREATE SCHEMA capture');

CREATE TABLE surf.StorageFormatInfo (
    Singleton tinyint NOT NULL CONSTRAINT PK_StorageFormatInfo PRIMARY KEY CHECK (Singleton = 1),
    FormatIdentifier nvarchar(64) NOT NULL,
    SchemaVersion int NOT NULL CHECK (SchemaVersion > 0),
    MinimumReaderVersion int NOT NULL CHECK (MinimumReaderVersion > 0),
    MinimumWriterVersion int NOT NULL CHECK (MinimumWriterVersion > 0),
    State varchar(24) NOT NULL CHECK (State IN ('Migrating', 'Ready')),
    DatabaseIdentity uniqueidentifier NOT NULL,
    CompletedMigrationIdentity uniqueidentifier NULL,
    CreatedAtUtc datetimeoffset(7) NOT NULL DEFAULT SYSDATETIMEOFFSET());

CREATE TABLE surf.SchemaMigration (
    SchemaVersion int NOT NULL CONSTRAINT PK_SchemaMigration PRIMARY KEY,
    ScriptChecksum binary(32) NOT NULL,
    AppliedAtUtc datetimeoffset(7) NOT NULL DEFAULT SYSDATETIMEOFFSET());

CREATE TABLE surf.UserProfile (
    ProfileKey bigint NOT NULL CONSTRAINT PK_UserProfile PRIMARY KEY CHECK (ProfileKey = 1),
    ProfileIdentity uniqueidentifier NOT NULL,
    LastActiveScopeId nvarchar(max) NULL);
INSERT surf.UserProfile(ProfileKey, ProfileIdentity, LastActiveScopeId) VALUES (1, NEWID(), NULL);

CREATE TABLE surf.TextContent (
    ContentKey bigint IDENTITY NOT NULL CONSTRAINT PK_TextContent PRIMARY KEY,
    ContentHash binary(32) NOT NULL,
    HashEncodingVersion int NOT NULL CHECK (HashEncodingVersion = 1),
    CharacterCount bigint NOT NULL CHECK (CharacterCount >= 0),
    ByteCount bigint NOT NULL CHECK (ByteCount >= 0),
    LineCount bigint NOT NULL CHECK (LineCount >= 1),
    Text nvarchar(max) NOT NULL);
CREATE INDEX IX_TextContent_Hash ON surf.TextContent(ContentHash, ByteCount);

CREATE TABLE surf.Asset (
    AssetKey bigint IDENTITY NOT NULL CONSTRAINT PK_Asset PRIMARY KEY,
    ContentHash binary(32) NOT NULL,
    ByteCount bigint NOT NULL CHECK (ByteCount >= 0),
    MediaType nvarchar(128) NOT NULL,
    Bytes varbinary(max) NOT NULL);
CREATE INDEX IX_Asset_Hash ON surf.Asset(ContentHash, ByteCount);

CREATE TABLE surf.MigrationRun (
    MigrationIdentity uniqueidentifier NOT NULL CONSTRAINT PK_MigrationRun PRIMARY KEY,
    SourceFingerprint binary(32) NOT NULL,
    ConverterVersion int NOT NULL,
    SourceDatabaseName nvarchar(max) NOT NULL,
    Status varchar(24) NOT NULL CHECK (Status IN ('Converting', 'Validating', 'Validated', 'Complete', 'Failed', 'Cancelled')),
    StartedAtUtc datetimeoffset(7) NOT NULL DEFAULT SYSDATETIMEOFFSET(),
    FinishedAtUtc datetimeoffset(7) NULL);

CREATE TABLE surf.MigrationCheckpoint (
    MigrationIdentity uniqueidentifier NOT NULL CONSTRAINT FK_Checkpoint_Run REFERENCES surf.MigrationRun(MigrationIdentity),
    SourceDocument nvarchar(128) NOT NULL,
    SourceHash binary(32) NOT NULL,
    LastOrdinal bigint NOT NULL CHECK (LastOrdinal >= -1),
    RecordCount bigint NOT NULL CHECK (RecordCount >= 0),
    DataRowCount bigint NOT NULL CHECK (DataRowCount >= 0),
    UpdatedAtUtc datetimeoffset(7) NOT NULL DEFAULT SYSDATETIMEOFFSET(),
    CONSTRAINT PK_MigrationCheckpoint PRIMARY KEY (MigrationIdentity, SourceDocument));

CREATE TABLE surf.MigrationIssue (
    IssueKey bigint IDENTITY NOT NULL CONSTRAINT PK_MigrationIssue PRIMARY KEY,
    MigrationIdentity uniqueidentifier NOT NULL CONSTRAINT FK_MigrationIssue_Run REFERENCES surf.MigrationRun(MigrationIdentity),
    Severity varchar(16) NOT NULL CHECK (Severity IN ('Warning', 'Error')),
    SourceDocument nvarchar(128) NOT NULL,
    SourceOrdinal bigint NULL,
    IssueCode nvarchar(128) NOT NULL,
    Message nvarchar(2048) NOT NULL);

CREATE TABLE surf.MigrationIdentityMap (
    MigrationIdentity uniqueidentifier NOT NULL CONSTRAINT FK_IdentityMap_Run REFERENCES surf.MigrationRun(MigrationIdentity),
    SourceIdentityHash binary(32) NOT NULL,
    SourceIdentity nvarchar(max) NOT NULL,
    EntityKind nvarchar(128) NOT NULL,
    DestinationKey bigint NOT NULL,
    CONSTRAINT PK_MigrationIdentityMap PRIMARY KEY (MigrationIdentity, SourceIdentityHash));

CREATE TABLE surf.SourceProvenance (
    MigrationIdentity uniqueidentifier NOT NULL CONSTRAINT FK_Provenance_Run REFERENCES surf.MigrationRun(MigrationIdentity),
    DocumentKey nvarchar(128) NOT NULL,
    SourceKind varchar(16) NOT NULL CHECK (SourceKind IN ('Sql', 'Local', 'Package', 'Default')),
    SourceSchemaVersion int NULL,
    OriginalTimestamp datetimeoffset(7) NULL,
    ContentHash binary(32) NOT NULL,
    ByteCount bigint NOT NULL,
    CONSTRAINT PK_SourceProvenance PRIMARY KEY (MigrationIdentity, DocumentKey));
