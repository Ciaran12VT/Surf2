-- Optional derived runtime extension. Never included in the v1 migration checksum.
CREATE TABLE surf.ReferenceCompletionExtension (
    Singleton tinyint NOT NULL PRIMARY KEY CHECK (Singleton=1),
    Version int NOT NULL,
    Checksum binary(32) NOT NULL);
CREATE TABLE surf.ReferenceResourceCompletion (
    ScopeResourceKey bigint NOT NULL PRIMARY KEY REFERENCES surf.ScopeResource(ScopeResourceKey) ON DELETE CASCADE,
    DependencyHash binary(32) NOT NULL,
    SourceVersion binary(8) NOT NULL,
    DocumentCount bigint NOT NULL CHECK (DocumentCount>=0),
    CompletedAtUtc datetimeoffset(7) NOT NULL DEFAULT SYSDATETIMEOFFSET());
CREATE INDEX IX_SnapshotResource_ReferenceVersion ON surf.SnapshotResource(SnapshotKey,RowVersion DESC);

-- Invalidation is set-based and transactional, including mutations made by older Surf versions.
EXEC(N'CREATE TRIGGER surf.TR_ReferenceCompletion_Document ON surf.Document AFTER UPDATE, DELETE AS
BEGIN
 SET NOCOUNT ON;
 DELETE c FROM surf.ReferenceResourceCompletion c JOIN surf.ResourceDocument m ON m.ScopeResourceKey=c.ScopeResourceKey
 WHERE EXISTS(SELECT 1 FROM deleted d WHERE d.DocumentKey=m.DocumentKey);
END');
EXEC(N'CREATE TRIGGER surf.TR_ReferenceCompletion_Membership ON surf.ResourceDocument AFTER INSERT, UPDATE, DELETE AS
BEGIN
 SET NOCOUNT ON;
 DELETE c FROM surf.ReferenceResourceCompletion c WHERE
 EXISTS(SELECT 1 FROM inserted i WHERE i.ScopeResourceKey=c.ScopeResourceKey) OR
 EXISTS(SELECT 1 FROM deleted d WHERE d.ScopeResourceKey=c.ScopeResourceKey);
END');
EXEC(N'CREATE TRIGGER surf.TR_ReferenceCompletion_Locator ON surf.DocumentLocator AFTER INSERT, UPDATE, DELETE AS
BEGIN
 SET NOCOUNT ON;
 DELETE c FROM surf.ReferenceResourceCompletion c WHERE
 EXISTS(SELECT 1 FROM inserted i WHERE i.ScopeResourceKey=c.ScopeResourceKey) OR
 EXISTS(SELECT 1 FROM deleted d WHERE d.ScopeResourceKey=c.ScopeResourceKey);
END');
-- Authoritative tables intentionally have no triggers: existing writers use OUTPUT without INTO.
-- Their dependency hashes/current source rowversion heads are compared in the batched reader.
