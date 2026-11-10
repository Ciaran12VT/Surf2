-- Applied after Core, Snapshots and Capture. No legacy source or external database is touched.
CREATE TABLE surf.StateCatalogueGeneration (
    ProfileKey bigint NOT NULL REFERENCES surf.UserProfile(ProfileKey),
    Kind int NOT NULL CHECK (Kind BETWEEN 0 AND 2), -- Scopes, diagrams, workbenches
    PublicationId uniqueidentifier NOT NULL,
    Version rowversion NOT NULL,
    PRIMARY KEY (ProfileKey, Kind));

CREATE TABLE surf.ScopeCatalogueState (
    ProfileKey bigint NOT NULL PRIMARY KEY REFERENCES surf.UserProfile(ProfileKey),
    SchemaVersion int NOT NULL,
    PublicationId uniqueidentifier NOT NULL,
    Version rowversion NOT NULL);

CREATE TABLE surf.Scope (
    ScopeKey bigint IDENTITY NOT NULL PRIMARY KEY,
    ProfileKey bigint NOT NULL REFERENCES surf.UserProfile(ProfileKey),
    PublicIdentity uniqueidentifier NOT NULL UNIQUE DEFAULT NEWID(),
    ScopeId nvarchar(max) NOT NULL,
    Name nvarchar(max) NOT NULL,
    DescriptionContentKey bigint NOT NULL REFERENCES surf.TextContent(ContentKey),
    SortOrdinal bigint NOT NULL CHECK (SortOrdinal >= 0),
    PublicationId uniqueidentifier NOT NULL,
    Version rowversion NOT NULL);
CREATE INDEX IX_Scope_Order ON surf.Scope(ProfileKey, SortOrdinal, ScopeKey);

CREATE TABLE surf.Diagram (
    DiagramKey bigint IDENTITY NOT NULL PRIMARY KEY,
    ProfileKey bigint NOT NULL REFERENCES surf.UserProfile(ProfileKey),
    PublicIdentity uniqueidentifier NOT NULL UNIQUE DEFAULT NEWID(),
    DiagramId nvarchar(max) NOT NULL,
    Name nvarchar(max) NOT NULL,
    SortOrdinal bigint NOT NULL CHECK (SortOrdinal >= 0),
    CurrentRevisionKey bigint NULL,
    PublicationId uniqueidentifier NOT NULL,
    Version rowversion NOT NULL);
CREATE INDEX IX_Diagram_Order ON surf.Diagram(ProfileKey, SortOrdinal, DiagramKey);

CREATE TABLE surf.ScopeResource (
    ScopeResourceKey bigint IDENTITY NOT NULL PRIMARY KEY,
    ScopeKey bigint NOT NULL REFERENCES surf.Scope(ScopeKey),
    SortOrdinal bigint NOT NULL CHECK (SortOrdinal >= 0),
    ResourceId nvarchar(max) NOT NULL,
    Kind int NOT NULL CHECK (Kind BETWEEN 0 AND 3), -- Folder, File, DatabaseSnapshot, Diagram
    Path nvarchar(max) NOT NULL,
    DisplayNameOverride nvarchar(max) NOT NULL,
    DetailsOverrideContentKey bigint NOT NULL REFERENCES surf.TextContent(ContentKey),
    AddedAtUtc datetimeoffset(7) NOT NULL,
    IncludeChildren bit NOT NULL,
    SnapshotKey bigint NULL REFERENCES surf.DatabaseSnapshot(SnapshotKey),
    DiagramKey bigint NULL REFERENCES surf.Diagram(DiagramKey),
    CHECK ((SnapshotKey IS NULL OR Kind = 2) AND (DiagramKey IS NULL OR Kind = 3)),
    UNIQUE (ScopeKey, SortOrdinal),
    UNIQUE (ScopeKey, ScopeResourceKey, Kind));
CREATE INDEX IX_ScopeResource_Snapshot ON surf.ScopeResource(SnapshotKey) WHERE SnapshotKey IS NOT NULL;
CREATE INDEX IX_ScopeResource_Diagram ON surf.ScopeResource(DiagramKey) WHERE DiagramKey IS NOT NULL;

CREATE TABLE surf.VirtualFolder (
    VirtualFolderKey bigint IDENTITY NOT NULL PRIMARY KEY,
    ScopeKey bigint NOT NULL REFERENCES surf.Scope(ScopeKey),
    SortOrdinal bigint NOT NULL CHECK (SortOrdinal >= 0),
    VirtualFolderId nvarchar(max) NOT NULL,
    Name nvarchar(max) NOT NULL,
    ParentNodeKey nvarchar(max) NOT NULL,
    ParentVirtualFolderKey bigint NULL,
    ParentScopeResourceKey bigint NULL,
    ParentResourceKind int NULL,
    ParentResolution int NOT NULL DEFAULT 0 CHECK (ParentResolution BETWEEN 0 AND 4),
    CHECK ((ParentResolution=1 AND ((ParentVirtualFolderKey IS NOT NULL AND ParentScopeResourceKey IS NULL AND ParentResourceKind IS NULL)
        OR (ParentVirtualFolderKey IS NULL AND ParentScopeResourceKey IS NOT NULL AND ParentResourceKind IS NOT NULL AND ParentResourceKind=0)))
        OR (ParentResolution<>1 AND ParentVirtualFolderKey IS NULL AND ParentScopeResourceKey IS NULL AND ParentResourceKind IS NULL)),
    UNIQUE (ScopeKey, SortOrdinal),
    UNIQUE (ScopeKey, VirtualFolderKey),
    FOREIGN KEY (ScopeKey, ParentScopeResourceKey, ParentResourceKind) REFERENCES surf.ScopeResource(ScopeKey, ScopeResourceKey, Kind));
ALTER TABLE surf.VirtualFolder ADD CONSTRAINT FK_VirtualFolder_Parent
    FOREIGN KEY (ScopeKey, ParentVirtualFolderKey) REFERENCES surf.VirtualFolder(ScopeKey, VirtualFolderKey);
CREATE INDEX IX_VirtualFolder_Parent ON surf.VirtualFolder(ScopeKey, ParentVirtualFolderKey) WHERE ParentVirtualFolderKey IS NOT NULL;
CREATE TABLE surf.VirtualFolderMember (
    ScopeKey bigint NOT NULL,
    VirtualFolderKey bigint NOT NULL,
    SortOrdinal bigint NOT NULL CHECK (SortOrdinal >= 0),
    ChildNodeKey nvarchar(max) NOT NULL,
    ChildVirtualFolderKey bigint NULL,
    ChildScopeResourceKey bigint NULL,
    ChildResourceKind int NULL,
    ChildResolution int NOT NULL DEFAULT 0 CHECK (ChildResolution BETWEEN 0 AND 4),
    CHECK ((ChildResolution=1 AND ((ChildVirtualFolderKey IS NOT NULL AND ChildScopeResourceKey IS NULL AND ChildResourceKind IS NULL)
        OR (ChildVirtualFolderKey IS NULL AND ChildScopeResourceKey IS NOT NULL AND ChildResourceKind IS NOT NULL AND ChildResourceKind=0)))
        OR (ChildResolution<>1 AND ChildVirtualFolderKey IS NULL AND ChildScopeResourceKey IS NULL AND ChildResourceKind IS NULL)),
    FOREIGN KEY (ScopeKey, VirtualFolderKey) REFERENCES surf.VirtualFolder(ScopeKey, VirtualFolderKey),
    FOREIGN KEY (ScopeKey, ChildVirtualFolderKey) REFERENCES surf.VirtualFolder(ScopeKey, VirtualFolderKey),
    FOREIGN KEY (ScopeKey, ChildScopeResourceKey, ChildResourceKind) REFERENCES surf.ScopeResource(ScopeKey, ScopeResourceKey, Kind),
    PRIMARY KEY (VirtualFolderKey, SortOrdinal));
CREATE INDEX IX_VirtualFolderMember_Scope ON surf.VirtualFolderMember(ScopeKey, VirtualFolderKey, SortOrdinal);
CREATE INDEX IX_VirtualFolderMember_Child ON surf.VirtualFolderMember(ScopeKey, ChildVirtualFolderKey) WHERE ChildVirtualFolderKey IS NOT NULL;

CREATE TABLE surf.Workbench (
    WorkbenchKey bigint IDENTITY NOT NULL PRIMARY KEY,
    ProfileKey bigint NOT NULL REFERENCES surf.UserProfile(ProfileKey),
    PublicIdentity uniqueidentifier NOT NULL UNIQUE DEFAULT NEWID(),
    SortOrdinal bigint NOT NULL CHECK (SortOrdinal >= 0),
    WorkbenchId nvarchar(max) NOT NULL,
    Name nvarchar(max) NOT NULL,
    IsDefaultForScope bit NOT NULL,
    CreatedAtUtc datetimeoffset(7) NOT NULL,
    UpdatedAtUtc datetimeoffset(7) NOT NULL,
    SavedAtUtc datetimeoffset(7) NOT NULL,
    ScopeId nvarchar(max) NOT NULL,
    ScopeName nvarchar(max) NOT NULL,
    ResolvedScopeKey bigint NULL REFERENCES surf.Scope(ScopeKey),
    ScopeResolution int NOT NULL DEFAULT 0 CHECK (ScopeResolution BETWEEN 0 AND 4),
    CHECK ((ScopeResolution=1 AND ResolvedScopeKey IS NOT NULL) OR (ScopeResolution<>1 AND ResolvedScopeKey IS NULL)),
    IsCodeViewVisible bit NOT NULL,
    IsDiagramViewVisible bit NOT NULL,
    ActiveWorkspaceView nvarchar(max) NOT NULL,
    WorkspaceSplitOrientation nvarchar(max) NOT NULL,
    CodeViewMode nvarchar(max) NOT NULL,
    PinnedExplorerDetailTab nvarchar(max) NOT NULL,
    ReferenceConnectionLinesEnabled bit NOT NULL,
    CodeCanvasZoom float(53) NOT NULL,
    CodeViewportHorizontalOffset float(53) NOT NULL,
    CodeViewportVerticalOffset float(53) NOT NULL,
    ActiveDocumentPath nvarchar(max) NOT NULL,
    ActiveDiagramId nvarchar(max) NOT NULL,
    ActiveDiagramName nvarchar(max) NOT NULL,
    IsDiagramLocked bit NOT NULL,
    DiagramCanvasZoom float(53) NOT NULL,
    DiagramViewportHorizontalOffset float(53) NOT NULL,
    DiagramViewportVerticalOffset float(53) NOT NULL,
    EmbeddedDiagramRevisionKey bigint NULL,
    PublicationId uniqueidentifier NOT NULL,
    Version rowversion NOT NULL);
CREATE INDEX IX_Workbench_Order ON surf.Workbench(ProfileKey, SortOrdinal, WorkbenchKey);
CREATE INDEX IX_Workbench_Recent ON surf.Workbench(ProfileKey, UpdatedAtUtc DESC, WorkbenchKey);
CREATE INDEX IX_Workbench_Scope ON surf.Workbench(ResolvedScopeKey, UpdatedAtUtc DESC, WorkbenchKey);

CREATE TABLE surf.DiagramRevision (
    DiagramRevisionKey bigint IDENTITY NOT NULL PRIMARY KEY,
    DiagramKey bigint NULL REFERENCES surf.Diagram(DiagramKey),
    WorkbenchKey bigint NULL REFERENCES surf.Workbench(WorkbenchKey),
    Origin int NOT NULL CHECK (Origin IN (0, 1)), -- Current diagram publication, embedded workbench copy
    PublicationId uniqueidentifier NOT NULL,
    DiagramId nvarchar(max) NOT NULL,
    Name nvarchar(max) NOT NULL,
    CreatedAtUtc datetimeoffset(7) NOT NULL,
    UpdatedAtUtc datetimeoffset(7) NOT NULL,
    CanvasZoom float(53) NOT NULL,
    ViewportHorizontalOffset float(53) NOT NULL,
    ViewportVerticalOffset float(53) NOT NULL,
    CHECK ((Origin = 0 AND DiagramKey IS NOT NULL AND WorkbenchKey IS NULL)
        OR (Origin = 1 AND WorkbenchKey IS NOT NULL AND DiagramKey IS NULL)),
    UNIQUE (DiagramKey, DiagramRevisionKey),
    UNIQUE (WorkbenchKey, DiagramRevisionKey));
ALTER TABLE surf.Diagram ADD CONSTRAINT FK_Diagram_CurrentRevision
    FOREIGN KEY (DiagramKey, CurrentRevisionKey) REFERENCES surf.DiagramRevision(DiagramKey, DiagramRevisionKey);
ALTER TABLE surf.Workbench ADD CONSTRAINT FK_Workbench_EmbeddedRevision
    FOREIGN KEY (WorkbenchKey, EmbeddedDiagramRevisionKey) REFERENCES surf.DiagramRevision(WorkbenchKey, DiagramRevisionKey);

-- All subtype fields are retained even when the current discriminator does not display them.
CREATE TABLE surf.DiagramObject (
    DiagramObjectKey bigint IDENTITY NOT NULL PRIMARY KEY,
    DiagramRevisionKey bigint NOT NULL REFERENCES surf.DiagramRevision(DiagramRevisionKey),
    SortOrdinal bigint NOT NULL CHECK (SortOrdinal >= 0),
    Id nvarchar(max) NOT NULL,
    ObjectType int NOT NULL CHECK (ObjectType BETWEEN 0 AND 6),
    ZIndex int NOT NULL,
    WorkflowId nvarchar(max) NOT NULL,
    WorkflowItemId nvarchar(max) NOT NULL,
    PortalName nvarchar(max) NOT NULL,
    PairedPortalDiagramId nvarchar(max) NOT NULL,
    PairedPortalObjectId nvarchar(max) NOT NULL,
    ShapeKind int NOT NULL CHECK (ShapeKind IN (0, 1)),
    ImageDefinitionId nvarchar(max) NOT NULL,
    ImageName nvarchar(max) NOT NULL,
    ImageDataBase64ContentKey bigint NOT NULL REFERENCES surf.TextContent(ContentKey),
    ImageAssetKey bigint NULL REFERENCES surf.Asset(AssetKey),
    PastedImageFileName nvarchar(max) NOT NULL,
    PastedAssetKey bigint NULL REFERENCES surf.Asset(AssetKey),
    PastedImageResolution int NOT NULL CHECK (PastedImageResolution BETWEEN 0 AND 3),
    PastedFallbackAssetKey bigint NULL REFERENCES surf.Asset(AssetKey),
    LabelTextContentKey bigint NOT NULL REFERENCES surf.TextContent(ContentKey),
    LabelFontSize float(53) NOT NULL,
    OutlineColorText nvarchar(max) NOT NULL,
    BackColorText nvarchar(max) NOT NULL,
    HasEndArrow bit NOT NULL,
    IsLineLoose bit NOT NULL,
    LineStartX float(53) NOT NULL,
    LineStartY float(53) NOT NULL,
    LineEndX float(53) NOT NULL,
    LineEndY float(53) NOT NULL,
    IsTethered bit NOT NULL,
    LabelAnchorX float(53) NOT NULL,
    LabelAnchorY float(53) NOT NULL,
    LabelBoxLeft float(53) NOT NULL,
    LabelBoxTop float(53) NOT NULL,
    LabelBoxWidth float(53) NOT NULL,
    LabelBoxHeight float(53) NOT NULL,
    [Left] float(53) NOT NULL,
    [Top] float(53) NOT NULL,
    Width float(53) NOT NULL,
    Height float(53) NOT NULL,
    MetadataLink nvarchar(max) NOT NULL,
    DocumentationXamlContentKey bigint NOT NULL REFERENCES surf.TextContent(ContentKey),
    UNIQUE (DiagramRevisionKey, SortOrdinal),
    UNIQUE (DiagramRevisionKey, DiagramObjectKey),
    CHECK ((PastedImageResolution=0 AND PastedAssetKey IS NULL AND PastedFallbackAssetKey IS NULL)
        OR (PastedImageResolution=1 AND DATALENGTH(PastedImageFileName)>0 AND PastedAssetKey IS NOT NULL AND PastedFallbackAssetKey IS NULL)
        OR (PastedImageResolution IN (2,3) AND DATALENGTH(PastedImageFileName)>0 AND PastedAssetKey IS NULL AND PastedFallbackAssetKey IS NOT NULL)));
CREATE INDEX IX_DiagramObject_Layer ON surf.DiagramObject(DiagramRevisionKey, ZIndex, SortOrdinal, DiagramObjectKey);

CREATE TABLE surf.Workflow (
    WorkflowKey bigint IDENTITY NOT NULL PRIMARY KEY,
    DiagramRevisionKey bigint NOT NULL REFERENCES surf.DiagramRevision(DiagramRevisionKey),
    SortOrdinal bigint NOT NULL CHECK (SortOrdinal >= 0),
    WorkflowId nvarchar(max) NOT NULL,
    WorkflowName nvarchar(max) NOT NULL,
    CreatedAtUtc datetimeoffset(7) NOT NULL,
    UpdatedAtUtc datetimeoffset(7) NOT NULL,
    AreMarkersVisible bit NOT NULL,
    UNIQUE (DiagramRevisionKey, SortOrdinal),
    UNIQUE (DiagramRevisionKey, WorkflowKey));
CREATE TABLE surf.WorkflowItem (
    WorkflowItemKey bigint IDENTITY NOT NULL PRIMARY KEY,
    DiagramRevisionKey bigint NOT NULL,
    WorkflowKey bigint NOT NULL,
    SortOrdinal bigint NOT NULL CHECK (SortOrdinal >= 0),
    WorkflowItemId nvarchar(max) NOT NULL,
    MarkerDiagramObjectId nvarchar(max) NOT NULL,
    ItemNumber int NOT NULL,
    ItemDescriptionContentKey bigint NOT NULL REFERENCES surf.TextContent(ContentKey),
    ItemDocumentationXamlContentKey bigint NOT NULL REFERENCES surf.TextContent(ContentKey),
    FOREIGN KEY (DiagramRevisionKey, WorkflowKey) REFERENCES surf.Workflow(DiagramRevisionKey, WorkflowKey),
    UNIQUE (WorkflowKey, SortOrdinal),
    UNIQUE (DiagramRevisionKey, WorkflowItemKey),
    UNIQUE (DiagramRevisionKey, WorkflowKey, WorkflowItemKey));

-- Resolution states: 0 None, 1 Resolved, 2 Missing, 3 Ambiguous, 4 ContextMismatch.
-- Raw source IDs remain on DiagramObject and WorkflowItem regardless of resolution.
CREATE TABLE surf.DiagramWorkflowBinding (
    DiagramRevisionKey bigint NOT NULL,
    DiagramObjectKey bigint NOT NULL PRIMARY KEY,
    WorkflowKey bigint NULL,
    WorkflowItemKey bigint NULL,
    WorkflowResolution int NOT NULL CHECK (WorkflowResolution BETWEEN 0 AND 4),
    ItemResolution int NOT NULL CHECK (ItemResolution BETWEEN 0 AND 4),
    FOREIGN KEY (DiagramRevisionKey, DiagramObjectKey) REFERENCES surf.DiagramObject(DiagramRevisionKey, DiagramObjectKey),
    FOREIGN KEY (DiagramRevisionKey, WorkflowKey) REFERENCES surf.Workflow(DiagramRevisionKey, WorkflowKey),
    FOREIGN KEY (DiagramRevisionKey, WorkflowKey, WorkflowItemKey) REFERENCES surf.WorkflowItem(DiagramRevisionKey, WorkflowKey, WorkflowItemKey),
    CHECK ((WorkflowResolution=1 AND WorkflowKey IS NOT NULL) OR (WorkflowResolution<>1 AND WorkflowKey IS NULL)),
    CHECK ((ItemResolution=1 AND WorkflowResolution=1 AND WorkflowItemKey IS NOT NULL) OR (ItemResolution<>1 AND WorkflowItemKey IS NULL)));
CREATE INDEX IX_DiagramWorkflowBinding_Revision ON surf.DiagramWorkflowBinding(DiagramRevisionKey, DiagramObjectKey);
CREATE TABLE surf.WorkflowItemMarker (
    DiagramRevisionKey bigint NOT NULL,
    WorkflowKey bigint NOT NULL,
    WorkflowItemKey bigint NOT NULL PRIMARY KEY,
    MarkerDiagramObjectKey bigint NULL,
    Resolution int NOT NULL CHECK (Resolution BETWEEN 0 AND 4),
    FOREIGN KEY (DiagramRevisionKey, WorkflowKey, WorkflowItemKey) REFERENCES surf.WorkflowItem(DiagramRevisionKey, WorkflowKey, WorkflowItemKey),
    FOREIGN KEY (DiagramRevisionKey, MarkerDiagramObjectKey) REFERENCES surf.DiagramObject(DiagramRevisionKey, DiagramObjectKey),
    CHECK ((Resolution=1 AND MarkerDiagramObjectKey IS NOT NULL) OR (Resolution<>1 AND MarkerDiagramObjectKey IS NULL)));
CREATE INDEX IX_WorkflowItemMarker_Revision ON surf.WorkflowItemMarker(DiagramRevisionKey, WorkflowItemKey);
CREATE TABLE surf.DiagramPortalTarget (
    DiagramRevisionKey bigint NOT NULL,
    DiagramObjectKey bigint NOT NULL PRIMARY KEY,
    TargetDiagramKey bigint NULL REFERENCES surf.Diagram(DiagramKey),
    TargetRevisionKey bigint NULL,
    TargetObjectKey bigint NULL,
    Resolution int NOT NULL CHECK (Resolution BETWEEN 0 AND 4),
    FOREIGN KEY (DiagramRevisionKey, DiagramObjectKey) REFERENCES surf.DiagramObject(DiagramRevisionKey, DiagramObjectKey),
    FOREIGN KEY (TargetRevisionKey, TargetObjectKey) REFERENCES surf.DiagramObject(DiagramRevisionKey, DiagramObjectKey),
    CHECK ((Resolution=1 AND ((TargetDiagramKey IS NOT NULL AND TargetRevisionKey IS NULL AND TargetObjectKey IS NULL)
        OR (TargetDiagramKey IS NULL AND TargetRevisionKey IS NOT NULL AND TargetRevisionKey=DiagramRevisionKey AND TargetObjectKey IS NOT NULL)))
        OR (Resolution<>1 AND TargetDiagramKey IS NULL AND TargetRevisionKey IS NULL AND TargetObjectKey IS NULL)));
CREATE INDEX IX_DiagramPortalTarget_Revision ON surf.DiagramPortalTarget(DiagramRevisionKey, DiagramObjectKey);

CREATE TABLE surf.QueryItem (
    QueryItemKey bigint IDENTITY NOT NULL PRIMARY KEY,
    DiagramRevisionKey bigint NOT NULL,
    DiagramObjectKey bigint NULL,
    WorkflowItemKey bigint NULL,
    SortOrdinal bigint NOT NULL CHECK (SortOrdinal >= 0),
    QueryId nvarchar(max) NOT NULL,
    QueryNumber int NOT NULL,
    CreatedDateUtc datetimeoffset(7) NOT NULL,
    Status int NOT NULL CHECK (Status BETWEEN 0 AND 2),
    QueryDescriptionContentKey bigint NOT NULL REFERENCES surf.TextContent(ContentKey),
    FOREIGN KEY (DiagramRevisionKey, DiagramObjectKey) REFERENCES surf.DiagramObject(DiagramRevisionKey, DiagramObjectKey),
    FOREIGN KEY (DiagramRevisionKey, WorkflowItemKey) REFERENCES surf.WorkflowItem(DiagramRevisionKey, WorkflowItemKey),
    CHECK ((DiagramObjectKey IS NULL AND WorkflowItemKey IS NOT NULL)
        OR (DiagramObjectKey IS NOT NULL AND WorkflowItemKey IS NULL)));
CREATE UNIQUE INDEX IX_QueryItem_ObjectOrder ON surf.QueryItem(DiagramObjectKey, SortOrdinal) WHERE DiagramObjectKey IS NOT NULL;
CREATE UNIQUE INDEX IX_QueryItem_WorkflowOrder ON surf.QueryItem(WorkflowItemKey, SortOrdinal) WHERE WorkflowItemKey IS NOT NULL;

CREATE TABLE surf.WorkspaceSession (
    WorkspaceSessionKey bigint IDENTITY NOT NULL PRIMARY KEY,
    ProfileKey bigint NOT NULL UNIQUE REFERENCES surf.UserProfile(ProfileKey),
    LastFolderPath nvarchar(max) NULL,
    CanvasZoom float(53) NOT NULL,
    ViewportHorizontalOffset float(53) NOT NULL,
    ViewportVerticalOffset float(53) NOT NULL,
    PublicationId uniqueidentifier NOT NULL,
    Version rowversion NOT NULL);
CREATE TABLE surf.DocumentWindowState (
    DocumentWindowKey bigint IDENTITY NOT NULL PRIMARY KEY,
    WorkspaceSessionKey bigint NULL REFERENCES surf.WorkspaceSession(WorkspaceSessionKey),
    WorkbenchKey bigint NULL REFERENCES surf.Workbench(WorkbenchKey),
    SortOrdinal bigint NOT NULL CHECK (SortOrdinal >= 0),
    FilePath nvarchar(max) NOT NULL,
    BoundSnapshotKey bigint NULL,
    BoundResourceKey bigint NULL,
    TargetState int NOT NULL DEFAULT 0 CHECK (TargetState BETWEEN 0 AND 3),
    FOREIGN KEY (BoundResourceKey, BoundSnapshotKey) REFERENCES surf.SnapshotResource(ResourceKey, SnapshotKey),
    CHECK ((TargetState=1 AND BoundSnapshotKey IS NOT NULL AND BoundResourceKey IS NOT NULL)
        OR (TargetState<>1 AND BoundSnapshotKey IS NULL AND BoundResourceKey IS NULL)),
    DisplayName nvarchar(max) NOT NULL,
    [Left] float(53) NOT NULL,
    [Top] float(53) NOT NULL,
    Width float(53) NOT NULL,
    Height float(53) NOT NULL,
    FontSize float(53) NOT NULL,
    HorizontalOffset float(53) NOT NULL,
    VerticalOffset float(53) NOT NULL,
    CHECK ((WorkspaceSessionKey IS NULL AND WorkbenchKey IS NOT NULL)
        OR (WorkspaceSessionKey IS NOT NULL AND WorkbenchKey IS NULL)));
CREATE UNIQUE INDEX IX_DocumentWindow_SessionOrder ON surf.DocumentWindowState(WorkspaceSessionKey, SortOrdinal) WHERE WorkspaceSessionKey IS NOT NULL;
CREATE UNIQUE INDEX IX_DocumentWindow_WorkbenchOrder ON surf.DocumentWindowState(WorkbenchKey, SortOrdinal) WHERE WorkbenchKey IS NOT NULL;
CREATE TABLE surf.DocumentWindowFilter (
    DocumentWindowKey bigint NOT NULL REFERENCES surf.DocumentWindowState(DocumentWindowKey),
    SortOrdinal bigint NOT NULL CHECK (SortOrdinal >= 0),
    ColumnIndex int NOT NULL,
    FilterText nvarchar(max) NOT NULL,
    PRIMARY KEY (DocumentWindowKey, SortOrdinal),
    UNIQUE (DocumentWindowKey, ColumnIndex));
CREATE TABLE surf.WorkspaceUnloadedResource (
    WorkspaceUnloadedResourceKey bigint IDENTITY NOT NULL PRIMARY KEY,
    WorkspaceSessionKey bigint NULL REFERENCES surf.WorkspaceSession(WorkspaceSessionKey),
    WorkbenchKey bigint NULL REFERENCES surf.Workbench(WorkbenchKey),
    SortOrdinal bigint NOT NULL CHECK (SortOrdinal >= 0),
    ResourceId nvarchar(max) NOT NULL,
    CHECK ((WorkspaceSessionKey IS NULL AND WorkbenchKey IS NOT NULL)
        OR (WorkspaceSessionKey IS NOT NULL AND WorkbenchKey IS NULL)));
CREATE UNIQUE INDEX IX_UnloadedResource_SessionOrder ON surf.WorkspaceUnloadedResource(WorkspaceSessionKey, SortOrdinal) WHERE WorkspaceSessionKey IS NOT NULL;
CREATE UNIQUE INDEX IX_UnloadedResource_WorkbenchOrder ON surf.WorkspaceUnloadedResource(WorkbenchKey, SortOrdinal) WHERE WorkbenchKey IS NOT NULL;
CREATE TABLE surf.ReferenceConnectionLine (
    ReferenceConnectionLineKey bigint IDENTITY NOT NULL PRIMARY KEY,
    WorkbenchKey bigint NOT NULL REFERENCES surf.Workbench(WorkbenchKey),
    SortOrdinal bigint NOT NULL CHECK (SortOrdinal >= 0),
    ConnectionId nvarchar(max) NOT NULL,
    SourceFilePath nvarchar(max) NOT NULL,
    SourceBoundSnapshotKey bigint NULL,
    SourceBoundResourceKey bigint NULL,
    SourceTargetState int NOT NULL DEFAULT 0 CHECK (SourceTargetState BETWEEN 0 AND 3),
    FOREIGN KEY (SourceBoundResourceKey, SourceBoundSnapshotKey) REFERENCES surf.SnapshotResource(ResourceKey, SnapshotKey),
    CHECK ((SourceTargetState=1 AND SourceBoundSnapshotKey IS NOT NULL AND SourceBoundResourceKey IS NOT NULL)
        OR (SourceTargetState<>1 AND SourceBoundSnapshotKey IS NULL AND SourceBoundResourceKey IS NULL)),
    SourceLineNumber int NOT NULL,
    SourceStartColumnNumber int NOT NULL,
    SourceEndColumnNumber int NOT NULL,
    TargetFilePath nvarchar(max) NOT NULL,
    TargetBoundSnapshotKey bigint NULL,
    TargetBoundResourceKey bigint NULL,
    TargetTargetState int NOT NULL DEFAULT 0 CHECK (TargetTargetState BETWEEN 0 AND 3),
    FOREIGN KEY (TargetBoundResourceKey, TargetBoundSnapshotKey) REFERENCES surf.SnapshotResource(ResourceKey, SnapshotKey),
    CHECK ((TargetTargetState=1 AND TargetBoundSnapshotKey IS NOT NULL AND TargetBoundResourceKey IS NOT NULL)
        OR (TargetTargetState<>1 AND TargetBoundSnapshotKey IS NULL AND TargetBoundResourceKey IS NULL)),
    TargetLineNumber int NOT NULL,
    TargetStartColumnNumber int NOT NULL,
    TargetEndColumnNumber int NOT NULL,
    UNIQUE (WorkbenchKey, SortOrdinal));

CREATE TABLE surf.ApplicationPreference (
    ProfileKey bigint NOT NULL PRIMARY KEY REFERENCES surf.UserProfile(ProfileKey),
    Theme nvarchar(max) NOT NULL,
    LoadMostRecentWorkbenchOnStartup bit NOT NULL,
    IgnoreWhitespaceByDefault bit NOT NULL,
    IgnoreCaseByDefault bit NOT NULL,
    EnableInternalLogging bit NOT NULL,
    DefaultBackcolor nvarchar(max) NOT NULL,
    KeyboardShortcutVersion int NOT NULL,
    EnableCanvasCtrlMousePanning bit NOT NULL,
    EnableTabCtrlMouseScrolling bit NOT NULL,
    EnableTabCtrlShiftMouseAutoscrolling bit NOT NULL,
    EnableCodeShiftMouseAutoscrolling bit NOT NULL,
    EnableCodeCtrlShiftMouseScrollbarLockedScrolling bit NOT NULL,
    EnableCodeCanvasShiftMousePanning bit NOT NULL,
    EnableCodeCanvasCtrlShiftMouseZooming bit NOT NULL,
    EnableCodeViewCtrlPlusMinusNavigation bit NOT NULL,
    EnableCtrlNumberViewSwitching bit NOT NULL,
    EnableCodeTabCtrlASNavigation bit NOT NULL,
    EnableDiagramCtrlQSidebarToggle bit NOT NULL,
    EnableDiagramCtrlWWorkflowSidebar bit NOT NULL,
    EnableDiagramShiftMousePanning bit NOT NULL,
    EnableDiagramCtrlShiftMouseZooming bit NOT NULL,
    PublicationId uniqueidentifier NOT NULL,
    Version rowversion NOT NULL);
CREATE TABLE surf.ExtensionAppearance (
    ExtensionAppearanceKey bigint IDENTITY NOT NULL PRIMARY KEY,
    ProfileKey bigint NOT NULL REFERENCES surf.ApplicationPreference(ProfileKey),
    SortOrdinal bigint NOT NULL CHECK (SortOrdinal >= 0),
    Extension nvarchar(max) NOT NULL,
    Backcolor nvarchar(max) NOT NULL,
    Language nvarchar(max) NOT NULL,
    UNIQUE (ProfileKey, SortOrdinal));
CREATE TABLE surf.ReferenceStyle (
    ReferenceStyleKey bigint IDENTITY NOT NULL PRIMARY KEY,
    ProfileKey bigint NOT NULL REFERENCES surf.ApplicationPreference(ProfileKey),
    SortOrdinal bigint NOT NULL CHECK (SortOrdinal >= 0),
    Language nvarchar(max) NOT NULL,
    Kind int NOT NULL CHECK (Kind BETWEEN 0 AND 12),
    Foreground nvarchar(max) NOT NULL,
    IsBold bit NOT NULL,
    IsItalic bit NOT NULL,
    IsUnderline bit NOT NULL,
    UNIQUE (ProfileKey, SortOrdinal));
CREATE TABLE surf.DiagramImageDefinition (
    DiagramImageDefinitionKey bigint IDENTITY NOT NULL PRIMARY KEY,
    ProfileKey bigint NOT NULL REFERENCES surf.ApplicationPreference(ProfileKey),
    SortOrdinal bigint NOT NULL CHECK (SortOrdinal >= 0),
    Id nvarchar(max) NOT NULL,
    Name nvarchar(max) NOT NULL,
    RegexContentKey bigint NOT NULL REFERENCES surf.TextContent(ContentKey),
    MatchTarget nvarchar(max) NOT NULL,
    NameRegexContentKey bigint NOT NULL REFERENCES surf.TextContent(ContentKey),
    ContentRegexContentKey bigint NOT NULL REFERENCES surf.TextContent(ContentKey),
    ResourceTypeFilter nvarchar(max) NOT NULL,
    SortOrder int NOT NULL,
    OriginalFileName nvarchar(max) NOT NULL,
    ImageDataBase64ContentKey bigint NOT NULL REFERENCES surf.TextContent(ContentKey),
    AssetKey bigint NULL REFERENCES surf.Asset(AssetKey),
    UNIQUE (ProfileKey, SortOrdinal));
