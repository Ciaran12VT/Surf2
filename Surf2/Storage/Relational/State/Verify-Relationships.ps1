param([string]$AssemblyPath = (Join-Path $PSScriptRoot '../../../bin/Debug/net10.0-windows/Surf2.dll'))
$ErrorActionPreference = 'Stop'
# Compile only the pure resolver in memory against the real models. No MSBuild, bin/obj writes, or SQL access.
$models = [Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($AssemblyPath))
$sources = @(
    'global using System; global using System.Collections.Generic; global using System.Linq;'
    (Get-Content -Raw (Join-Path $PSScriptRoot 'StateContracts.cs'))
    (Get-Content -Raw (Join-Path $PSScriptRoot 'StateRelationshipResolver.cs'))
)
$trees = [Microsoft.CodeAnalysis.SyntaxTree[]]@($sources | ForEach-Object {
    [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText($_)
})
$paths = @([AppContext]::GetData('TRUSTED_PLATFORM_ASSEMBLIES') -split [IO.Path]::PathSeparator) + @($models.Location)
$references = [Microsoft.CodeAnalysis.MetadataReference[]]@($paths | Select-Object -Unique | ForEach-Object {
    [Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($_)
})
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new([Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create(('StateRelationshipChecks_' + [Guid]::NewGuid().ToString('N')), $trees, $references, $options)
$stream = [IO.MemoryStream]::new()
$result = $compilation.Emit($stream)
if (!$result.Success) { throw ($result.Diagnostics -join "`n") }
$assembly = [Reflection.Assembly]::Load($stream.ToArray())
$stream.Dispose()
$resolver = $assembly.GetType('Surf2.Storage.Relational.State.StateRelationshipResolver', $true)
$script:passed = 0
function Check([bool]$condition, [string]$name) {
    if (!$condition) { throw "FAILED: $name" }
    $script:passed++
    Write-Output "PASS: $name"
}
function Model([string]$name) { [Activator]::CreateInstance($models.GetType('Surf2.Models.' + $name, $true)) }
function Resolve([string]$method, $value) { $resolver.GetMethod($method).Invoke($null, [object[]]@($value)) }
function Status($value) { $value.ToString() }
function Fixture {
    $diagram = Model 'DiagramDocument'
    $diagram.DiagramId = 'SavedDiagram'
    $workflow = Model 'WorkflowDocument'
    $workflow.WorkflowId = 'Workflow'
    $item = Model 'WorkflowItem'
    $item.WorkflowItemId = 'Item'
    $item.MarkerDiagramObjectId = 'Marker'
    $workflow.Items.Add($item)
    $diagram.Workflows.Add($workflow)
    $marker = Model 'DiagramObjectSnapshot'
    $marker.Id = 'Marker'
    $marker.ObjectType = 4
    $marker.WorkflowId = 'WORKFLOW'
    $marker.WorkflowItemId = 'ITEM'
    $diagram.Objects.Add($marker)
    return $diagram
}
$diagram = Fixture
$plan = Resolve 'Workflows' $diagram
Check ((Status $plan.Item1[0].WorkflowResolution) -eq 'Resolved' -and $plan.Item1[0].WorkflowOrdinal -eq 0) 'Unique workflow resolves within selected revision, OrdinalIgnoreCase'
Check ((Status $plan.Item1[0].ItemResolution) -eq 'Resolved' -and $plan.Item1[0].ItemOrdinal -eq 0) 'Unique item resolves only inside selected workflow'
Check ((Status $plan.Item2[0].Resolution) -eq 'Resolved' -and $plan.Item2[0].ObjectOrdinal -eq 0) 'Reciprocal marker resolves within selected revision'
Check ($diagram.Objects[0].WorkflowId -ceq 'WORKFLOW' -and $diagram.Objects[0].WorkflowItemId -ceq 'ITEM') 'Raw relationship IDs are not normalized'
$duplicate = Model 'WorkflowDocument'
$duplicate.WorkflowId = 'workflow'
$diagram.Workflows.Add($duplicate)
$plan = Resolve 'Workflows' $diagram
Check ((Status $plan.Item1[0].WorkflowResolution) -eq 'Ambiguous' -and $null -eq $plan.Item1[0].WorkflowOrdinal) 'Duplicate workflow IDs have no arbitrary target'
Check ((Status $plan.Item1[0].ItemResolution) -eq 'Ambiguous' -and $null -eq $plan.Item1[0].ItemOrdinal) 'Ambiguous workflow prevents item binding'
Check ((Status $plan.Item2[0].Resolution) -eq 'Ambiguous' -and $null -eq $plan.Item2[0].ObjectOrdinal) 'Ambiguous owning context prevents marker binding'
$diagram = Fixture
$duplicate = Model 'WorkflowItem'
$duplicate.WorkflowItemId = 'item'
$duplicate.MarkerDiagramObjectId = 'Marker'
$diagram.Workflows[0].Items.Add($duplicate)
$plan = Resolve 'Workflows' $diagram
Check ((Status $plan.Item1[0].ItemResolution) -eq 'Ambiguous' -and $null -eq $plan.Item1[0].ItemOrdinal) 'Duplicate items inside one workflow remain ambiguous'
Check ((Status $plan.Item2[0].Resolution) -eq 'Ambiguous') 'Duplicate item context is explicit on marker relation'
$diagram = Fixture
$otherWorkflow = Model 'WorkflowDocument'
$otherWorkflow.WorkflowId = 'OtherWorkflow'
$otherItem = Model 'WorkflowItem'
$otherItem.WorkflowItemId = 'Item'
$otherItem.MarkerDiagramObjectId = 'Marker'
$otherWorkflow.Items.Add($otherItem)
$diagram.Workflows.Add($otherWorkflow)
$plan = Resolve 'Workflows' $diagram
Check ((Status $plan.Item1[0].ItemResolution) -eq 'Resolved') 'Equal item IDs in another workflow do not cause global binding'
Check ((Status $plan.Item2[1].Resolution) -eq 'ContextMismatch' -and $null -eq $plan.Item2[1].ObjectOrdinal) 'Marker cannot belong to another workflow context'
$duplicate = Model 'DiagramObjectSnapshot'
$duplicate.Id = 'marker'
$duplicate.ObjectType = 0
$diagram.Objects.Add($duplicate)
$plan = Resolve 'Workflows' $diagram
Check ((Status $plan.Item2[0].Resolution) -eq 'Ambiguous' -and $null -eq $plan.Item2[0].ObjectOrdinal) 'Duplicate object ID is ambiguous even across different object types'
$diagram = Fixture
$diagram.Objects[0].ObjectType = 0
$plan = Resolve 'Workflows' $diagram
Check ((Status $plan.Item1[0].WorkflowResolution) -eq 'ContextMismatch' -and $null -eq $plan.Item1[0].WorkflowOrdinal) 'Non-marker raw workflow fields retained without typed binding'
Check ((Status $plan.Item2[0].Resolution) -eq 'ContextMismatch') 'Marker target type validated'
$diagram = Fixture
$diagram.Objects[0].WorkflowId = 'Missing'
$plan = Resolve 'Workflows' $diagram
Check ((Status $plan.Item1[0].WorkflowResolution) -eq 'Missing' -and $null -eq $plan.Item1[0].WorkflowOrdinal) 'Missing workflow is explicit and does not bind globally'
$diagram = Model 'DiagramDocument'
$diagram.DiagramId = 'SavedDiagram'
$portal = Model 'DiagramObjectSnapshot'
$portal.ObjectType = 5
$portal.Id = 'Source'
$portal.PairedPortalDiagramId = 'SAVEDDIAGRAM'
$portal.PairedPortalObjectId = 'Target'
$target = Model 'DiagramObjectSnapshot'
$target.ObjectType = 5
$target.Id = 'Target'
$diagram.Objects.Add($portal)
$diagram.Objects.Add($target)
$plan = Resolve 'Portals' $diagram
Check ((Status $plan[0].Resolution) -eq 'Resolved' -and $plan[0].TargetObjectOrdinal -eq 1 -and $null -eq $plan[0].LogicalDiagramId) 'Self portal binds to exact saved revision, never a current diagram'
$portal.PairedPortalDiagramId = 'OtherDiagram'
$plan = Resolve 'Portals' $diagram
Check ($plan[0].LogicalDiagramId -ceq 'OtherDiagram' -and $null -eq $plan[0].TargetObjectOrdinal) 'Cross-diagram portal requests logical identity only, original object ID retained'
$portal.PairedPortalDiagramId = 'SavedDiagram'
$duplicate = Model 'DiagramObjectSnapshot'
$duplicate.Id = 'target'
$duplicate.ObjectType = 5
$diagram.Objects.Add($duplicate)
$plan = Resolve 'Portals' $diagram
Check ((Status $plan[0].Resolution) -eq 'Ambiguous' -and $null -eq $plan[0].TargetObjectOrdinal) 'Duplicate portal object IDs remain ambiguous within exact revision'
$diagram.Objects.RemoveAt(2)
$target.ObjectType = 0
$plan = Resolve 'Portals' $diagram
Check ((Status $plan[0].Resolution) -eq 'ContextMismatch' -and $null -eq $plan[0].TargetObjectOrdinal) 'Self portal cannot target a non-portal object'
$scope = Model 'Scope'
$parent = Model 'VirtualFolder'
$parent.VirtualFolderId = 'Parent'
$parent.ChildNodeKeys.Add('surf2://virtual-folder/Child')
$parent.ChildNodeKeys.Add('surf2://virtual-folder/Child')
$parent.ChildNodeKeys.Add('C:\untyped\file.txt')
$child = Model 'VirtualFolder'
$child.VirtualFolderId = 'Child'
$child.ParentNodeKey = 'SURF2://VIRTUAL-FOLDER/PARENT'
$scope.VirtualFolders.Add($parent)
$scope.VirtualFolders.Add($child)
$plan = Resolve 'Folders' $scope
Check ((Status $plan[0].Resolution) -eq 'None') 'Root parent remains raw without invented FK'
Check ($plan[1].TargetOrdinal -eq 1 -and $plan[2].TargetOrdinal -eq 1 -and $plan[1].MemberOrdinal -eq 0 -and $plan[2].MemberOrdinal -eq 1) 'Repeated ordered folder memberships independently target the same unique folder'
Check ((Status $plan[3].Resolution) -eq 'None' -and $null -eq $plan[3].TargetOrdinal) 'Filesystem node key remains raw and unbound'
Check ((Status $plan[4].Resolution) -eq 'Resolved' -and $plan[4].TargetOrdinal -eq 0) 'Virtual parent resolves within selected scope, OrdinalIgnoreCase'
$duplicate = Model 'VirtualFolder'
$duplicate.VirtualFolderId = 'child'
$scope.VirtualFolders.Add($duplicate)
$plan = Resolve 'Folders' $scope
Check ((Status $plan[1].Resolution) -eq 'Ambiguous' -and $null -eq $plan[1].TargetOrdinal) 'Duplicate virtual-folder IDs never select arbitrary membership target'
$parent.ParentNodeKey = 'surf2://virtual-folder/Parent'
$plan = Resolve 'Folders' $scope
Check ((Status $plan[0].Resolution) -eq 'ContextMismatch' -and $null -eq $plan[0].TargetOrdinal) 'Self parent retains raw key without invalid typed link'
$resource = Model 'ScopedResource'
$resource.Kind = 0
$resource.Path = 'C:\owned\folder'
$scope.Resources.Add($resource)
$parent.ParentNodeKey = 'C:\OWNED\FOLDER'
$parent.ChildNodeKeys.Add('C:\owned\folder')
$plan = Resolve 'Folders' $scope
Check ((Status $plan[0].Resolution) -eq 'Resolved' -and $plan[0].ResourceOrdinal -eq 0 -and $null -eq $plan[0].TargetOrdinal) 'Unique owned folder root becomes a typed parent resource'
Check ((Status $plan[4].Resolution) -eq 'Resolved' -and $plan[4].ResourceOrdinal -eq 0) 'Unique owned folder root becomes a typed membership resource'
$duplicate = Model 'ScopedResource'
$duplicate.Kind = 0
$duplicate.Path = 'C:\OWNED\FOLDER'
$scope.Resources.Add($duplicate)
$plan = Resolve 'Folders' $scope
Check ((Status $plan[0].Resolution) -eq 'Ambiguous' -and $null -eq $plan[0].ResourceOrdinal) 'Duplicate folder root aliases cannot pick an arbitrary scope entry'
$scope.Resources.RemoveAt(1)
$resource.Kind = 1
$plan = Resolve 'Folders' $scope
Check ((Status $plan[0].Resolution) -eq 'None' -and $null -eq $plan[0].ResourceOrdinal) 'File resource is not misbound as a folder root'
$ddl = Get-Content -Raw (Join-Path $PSScriptRoot '../Schema/001.State.sql')
Check ($ddl.Contains('FOREIGN KEY (DiagramRevisionKey, WorkflowKey, WorkflowItemKey)')) 'Workflow item FK enforces exact revision and workflow context'
Check ($ddl.Contains('FOREIGN KEY (DiagramRevisionKey, DiagramObjectKey)') -and $ddl.Contains('FOREIGN KEY (DiagramRevisionKey, WorkflowItemKey)')) 'Query owners have revision-constrained object/item FKs'
Check ($ddl.Contains('FOREIGN KEY (ScopeKey, ChildVirtualFolderKey)') -and $ddl.Contains('FOREIGN KEY (ScopeKey, ParentVirtualFolderKey)')) 'Folder FKs cannot escape selected scope'
Check ($ddl.Contains('TargetRevisionKey=DiagramRevisionKey')) 'Self portal FK cannot escape selected revision'
Check ($ddl.Contains('FOREIGN KEY (ScopeKey, ChildScopeResourceKey, ChildResourceKind)') -and $ddl.Contains('ChildResourceKind=0')) 'Membership FK enforces scope and folder resource kind'
foreach ($file in @('RelationalStateStore.cs', 'RelationalStateStore.Scopes.cs', 'RelationalStateStore.Diagrams.cs', 'RelationalStateStore.Layout.cs', 'RelationalStateStore.Settings.cs')) {
    $tree = [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText((Get-Content -Raw (Join-Path $PSScriptRoot $file)))
    foreach ($method in $tree.GetRoot().DescendantNodes() | Where-Object { $_ -is [Microsoft.CodeAnalysis.CSharp.Syntax.MethodDeclarationSyntax] }) {
        $name = $method.Identifier.ValueText
        if ($name -in @('Expected', 'ImportArguments', 'ExecuteAsync', 'InsertAsync', 'CreateScopeAsync', 'CreateDiagramAsync', 'CreateWorkbenchAsync', 'CreateWorkspaceAsync', 'CreateSettingsAsync')) {
            Check ($method.Body.Statements[0].ToString() -ceq '_session.RejectValidationWrite();') "$name rejects migration-validation writes before work"
        }
    }
}
Write-Output "Passed $script:passed relationship checks. No SQL connection or MSBuild was used."
