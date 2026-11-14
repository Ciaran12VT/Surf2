-- Schema 3: portable resource alternatives. Published schema 1/2 scripts are immutable.
-- Fold the known unversioned schema-2 preference additions into this frozen contract.
IF COL_LENGTH('surf.ApplicationPreference','DefaultDarkBackcolor') IS NULL
BEGIN
 ALTER TABLE surf.ApplicationPreference ADD DefaultDarkBackcolor nvarchar(max) NOT NULL CONSTRAINT DF_ApplicationPreference_DefaultDarkBackcolor DEFAULT N'';
 EXEC(N'UPDATE surf.ApplicationPreference SET DefaultDarkBackcolor=DefaultBackcolor;');
END;
IF COL_LENGTH('surf.ApplicationPreference','OpenDiagramDocumentationOnDoubleClick') IS NULL
 ALTER TABLE surf.ApplicationPreference ADD OpenDiagramDocumentationOnDoubleClick bit NOT NULL CONSTRAINT DF_ApplicationPreference_OpenDiagramDocumentation DEFAULT 1;
IF COL_LENGTH('surf.ApplicationPreference','ShowExplorerPreview') IS NULL
 ALTER TABLE surf.ApplicationPreference ADD ShowExplorerPreview bit NOT NULL CONSTRAINT DF_ApplicationPreference_ShowExplorerPreview DEFAULT 1;
IF COL_LENGTH('surf.ExtensionAppearance','DarkBackcolor') IS NULL
BEGIN
 ALTER TABLE surf.ExtensionAppearance ADD DarkBackcolor nvarchar(max) NOT NULL CONSTRAINT DF_ExtensionAppearance_DarkBackcolor DEFAULT N'';
 EXEC(N'UPDATE surf.ExtensionAppearance SET DarkBackcolor=Backcolor;');
END;
CREATE TABLE surf.SourceDefinition (
    SourceId uniqueidentifier NOT NULL PRIMARY KEY,
    Provider int NOT NULL CHECK (Provider BETWEEN 0 AND 3),
    ConfigurationRevision bigint NOT NULL CHECK (ConfigurationRevision > 0),
    Endpoint nvarchar(max) NOT NULL,
    Container nvarchar(max) NOT NULL,
    Prefix nvarchar(max) NOT NULL,
    Ref nvarchar(max) NOT NULL,
    Subfolder nvarchar(max) NOT NULL,
    DatabaseName nvarchar(max) NOT NULL,
    AuthenticationKind nvarchar(max) NOT NULL);
CREATE TABLE surf.ResourceContainer (
    ContainerId uniqueidentifier NOT NULL PRIMARY KEY,
    ScopeKey bigint NOT NULL REFERENCES surf.Scope(ScopeKey),
    Name nvarchar(max) NOT NULL,
    SortOrdinal bigint NOT NULL CHECK (SortOrdinal >= 0),
    DefaultResourceId uniqueidentifier NULL,
    UNIQUE (ScopeKey, ContainerId));
ALTER TABLE surf.ScopeResource ADD PublicId uniqueidentifier NOT NULL DEFAULT NEWID(),
    ContainerId uniqueidentifier NOT NULL DEFAULT NEWID(),
    SourceId uniqueidentifier NULL,
    PairedResourceId uniqueidentifier NULL;
ALTER TABLE surf.ScopeResource ADD CONSTRAINT UQ_ScopeResource_PublicId UNIQUE (PublicId);
EXEC(N'INSERT surf.ResourceContainer(ContainerId,ScopeKey,Name,SortOrdinal,DefaultResourceId)
SELECT r.ContainerId,r.ScopeKey,
 COALESCE(NULLIF(r.DisplayNameOverride,N''''),s.DisplayName,
 CASE WHEN r.Kind IN(0,1) THEN COALESCE(NULLIF(RIGHT(REPLACE(r.Path,N''/'',N''\''),CHARINDEX(N''\'',REVERSE(REPLACE(r.Path,N''/'',N''\''))+N''\'')-1),N''''),r.Path) ELSE r.Path END),
 r.SortOrdinal,r.PublicId FROM surf.ScopeResource r LEFT JOIN surf.DatabaseSnapshot s ON s.SnapshotKey=r.SnapshotKey;');
EXEC(N'UPDATE surf.ScopeResource SET SourceId=NEWID() WHERE Kind IN (0,1);');
EXEC(N'INSERT surf.SourceDefinition(SourceId,Provider,ConfigurationRevision,Endpoint,Container,Prefix,Ref,Subfolder,DatabaseName,AuthenticationKind)
SELECT SourceId,0,1,Path,N'''',N'''',N'''',N'''',N'''',N'''' FROM surf.ScopeResource WHERE SourceId IS NOT NULL;');
ALTER TABLE surf.ScopeResource ADD CONSTRAINT FK_ScopeResource_Container FOREIGN KEY(ScopeKey,ContainerId)
    REFERENCES surf.ResourceContainer(ScopeKey,ContainerId);
ALTER TABLE surf.ScopeResource ADD CONSTRAINT FK_ScopeResource_Source FOREIGN KEY(SourceId)
    REFERENCES surf.SourceDefinition(SourceId);
-- Drop only the old Kind range check; retain snapshot/diagram binding checks.
DECLARE @KindCheck sysname;
SELECT @KindCheck=c.name FROM sys.check_constraints c
WHERE c.parent_object_id=OBJECT_ID(N'surf.ScopeResource')
AND c.definition LIKE N'%[[]Kind[]]%>=%(0)%' AND c.definition LIKE N'%[[]Kind[]]%<=%(3)%';
IF @KindCheck IS NULL
    SELECT @KindCheck=c.name FROM sys.check_constraints c
    WHERE c.parent_object_id=OBJECT_ID(N'surf.ScopeResource') AND c.parent_column_id=COLUMNPROPERTY(OBJECT_ID(N'surf.ScopeResource'),N'Kind','ColumnId');
IF @KindCheck IS NULL THROW 51120,'The schema-2 resource kind constraint is missing.',1;
DECLARE @DropKindCheck nvarchar(max)=N'ALTER TABLE surf.ScopeResource DROP CONSTRAINT '+QUOTENAME(@KindCheck);
EXEC(@DropKindCheck);
ALTER TABLE surf.ScopeResource ADD CONSTRAINT CK_ScopeResource_Kind CHECK (Kind BETWEEN 0 AND 4);
CREATE TABLE surf.SnapshotCaptureEvent (
    EventId uniqueidentifier NOT NULL PRIMARY KEY,
    SnapshotKey bigint NOT NULL REFERENCES surf.DatabaseSnapshot(SnapshotKey),
    VersionKey bigint NULL,
    SourceId uniqueidentifier NULL,
    StartedAtUtc datetimeoffset(7) NOT NULL,
    CompletedAtUtc datetimeoffset(7) NULL,
    Outcome int NOT NULL CHECK (Outcome BETWEEN 0 AND 5),
    OriginJson nvarchar(max) NULL CHECK (OriginJson IS NULL OR ISJSON(OriginJson)=1),
    Details nvarchar(max) NULL,
    FOREIGN KEY (VersionKey,SnapshotKey) REFERENCES surf.SnapshotVersion(VersionKey,SnapshotKey));
CREATE INDEX IX_SnapshotCaptureEvent_History ON surf.SnapshotCaptureEvent(SnapshotKey,StartedAtUtc,EventId);
CREATE TABLE surf.SnapshotVersionOrigin (
    VersionKey bigint NOT NULL PRIMARY KEY REFERENCES surf.SnapshotVersion(VersionKey),
    OriginJson nvarchar(max) NOT NULL CHECK (ISJSON(OriginJson)=1));
