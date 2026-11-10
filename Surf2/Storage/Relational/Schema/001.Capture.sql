-- Apply after Core and Snapshots. The snapshot domain owns SnapshotResourceRevision.
CREATE TABLE surf.DataLayout (
    LayoutKey bigint IDENTITY NOT NULL CONSTRAINT PK_DataLayout PRIMARY KEY,
    LayoutHash binary(32) NOT NULL,
    EncodingVersion int NOT NULL CONSTRAINT CK_DataLayout_Encoding CHECK (EncodingVersion = 1),
    ColumnCount int NOT NULL CONSTRAINT CK_DataLayout_Columns CHECK (ColumnCount BETWEEN 0 AND 128),
    StorageTableName AS (CONVERT(varchar(5), 'Data_') + CONVERT(varchar(20), LayoutKey)) PERSISTED);
CREATE INDEX IX_DataLayout_Hash ON surf.DataLayout(LayoutHash);

CREATE TABLE surf.DataColumn (
    LayoutKey bigint NOT NULL CONSTRAINT FK_DataColumn_Layout REFERENCES surf.DataLayout(LayoutKey),
    ColumnOrdinal int NOT NULL CHECK (ColumnOrdinal BETWEEN 0 AND 127),
    SourceName nvarchar(max) NOT NULL CHECK (DATALENGTH(SourceName) <= 8192),
    SourceDataType nvarchar(max) NULL CHECK (DATALENGTH(SourceDataType) <= 8192),
    SourceMaxLength int NULL,
    SourcePrecision tinyint NULL,
    SourceScale int NULL,
    SourceNullable bit NULL,
    SourceOrdinal int NULL,
    SourceIdentity bit NULL,
    PhysicalColumnName AS ('C' + RIGHT('0000' + CONVERT(varchar(4), ColumnOrdinal + 1), 4)) PERSISTED,
    EncodingPolicy varchar(32) NOT NULL CHECK (EncodingPolicy = 'JsonScalarToken-v1'),
    CONSTRAINT PK_DataColumn PRIMARY KEY (LayoutKey, ColumnOrdinal));

CREATE TABLE surf.DataSet (
    DataSetKey bigint IDENTITY NOT NULL CONSTRAINT PK_DataSet PRIMARY KEY,
    RevisionKey bigint NOT NULL CONSTRAINT FK_DataSet_Revision REFERENCES surf.SnapshotResourceRevision(RevisionKey),
    LayoutKey bigint NOT NULL CONSTRAINT FK_DataSet_Layout REFERENCES surf.DataLayout(LayoutKey),
    -- Reported values are evidence, including inconsistent/negative legacy values.
    ReportedRowCount bigint NOT NULL,
    ActualRowCount bigint NOT NULL DEFAULT 0 CHECK (ActualRowCount >= 0),
    ExpectedActualRowCount bigint NULL CHECK (ExpectedActualRowCount >= 0),
    ImportedAtUtc datetimeoffset(7) NOT NULL,
    State varchar(16) NOT NULL CHECK (State IN ('Writing', 'Ready')),
    DisplayFormatVersion int NOT NULL CHECK (DisplayFormatVersion = 1),
    CONSTRAINT CK_DataSet_ReadyCount CHECK (State <> 'Ready' OR (ExpectedActualRowCount IS NOT NULL AND ActualRowCount = ExpectedActualRowCount)),
    CONSTRAINT UQ_DataSet_Revision UNIQUE (RevisionKey),
    CONSTRAINT UQ_DataSet_Layout UNIQUE (DataSetKey, LayoutKey));
CREATE INDEX IX_DataSet_Layout ON surf.DataSet(LayoutKey, DataSetKey);

-- Only exceptional tokens, not the normal cell store. PropertyOrdinal=-1 is a
-- bounded non-object root. Object property names/order are in the row descriptor.
CREATE TABLE surf.DataValueException (
    DataSetKey bigint NOT NULL CONSTRAINT FK_DataValueException_DataSet REFERENCES surf.DataSet(DataSetKey),
    RowOrdinal bigint NOT NULL CHECK (RowOrdinal >= 0),
    PropertyOrdinal int NOT NULL CHECK (PropertyOrdinal BETWEEN -1 AND 159),
    ValueKind tinyint NOT NULL CHECK (ValueKind BETWEEN 1 AND 7),
    RawToken nvarchar(max) NOT NULL CHECK (DATALENGTH(RawToken) <= 2097152),
    CONSTRAINT PK_DataValueException PRIMARY KEY (DataSetKey, RowOrdinal, PropertyOrdinal));
