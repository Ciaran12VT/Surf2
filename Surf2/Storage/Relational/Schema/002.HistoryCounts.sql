-- Derived runtime extension. Existing v1 schema checksums and version rows are unchanged.
CREATE TABLE surf.HistoryCountExtension (
    Singleton tinyint NOT NULL PRIMARY KEY CHECK (Singleton=1),
    Version int NOT NULL,
    Checksum binary(32) NOT NULL);

CREATE TABLE surf.HistoryFileLineCount (
    VersionKey bigint NOT NULL,
    SnapshotKey bigint NOT NULL,
    ResourceKey bigint NOT NULL CHECK (ResourceKey>0),
    Kind int NOT NULL CHECK (Kind BETWEEN 0 AND 5),
    InputHash binary(32) NOT NULL,
    AddedLines int NOT NULL CHECK (AddedLines>=0),
    RemovedLines int NOT NULL CHECK (RemovedLines>=0),
    PRIMARY KEY (VersionKey, ResourceKey, Kind, InputHash),
    FOREIGN KEY (VersionKey, SnapshotKey) REFERENCES surf.SnapshotVersion(VersionKey, SnapshotKey) ON DELETE CASCADE);

CREATE TABLE surf.HistoryInitialFileCount (
    VersionKey bigint NOT NULL,
    SnapshotKey bigint NOT NULL,
    ProjectionHash binary(32) NOT NULL,
    FileCount bigint NOT NULL CHECK (FileCount>=0),
    PRIMARY KEY (VersionKey, ProjectionHash),
    FOREIGN KEY (VersionKey, SnapshotKey) REFERENCES surf.SnapshotVersion(VersionKey, SnapshotKey) ON DELETE CASCADE);
