param([string]$AssemblyPath = (Join-Path $PSScriptRoot '../../../bin/Debug/net10.0-windows/Surf2.dll'))
$ErrorActionPreference = 'Stop'
$assembly = [Reflection.Assembly]::LoadFrom([IO.Path]::GetFullPath($AssemblyPath))
$binding = [Reflection.BindingFlags]'Public,NonPublic,Static,Instance'
$prefix = 'Surf2.Storage.Relational.State.'
$maps = $assembly.GetType($prefix + 'StateMaps', $true)
$budgetType = $assembly.GetType($prefix + 'StateBudget', $true)
$limitsType = $assembly.GetType($prefix + 'StateLimits', $true)
$limits = [Activator]::CreateInstance($limitsType)
$script:passed = 0
function Check([bool]$condition, [string]$name) {
    if (!$condition) { throw "FAILED: $name" }
    $script:passed++
    Write-Output "PASS: $name"
}
function Budget {
    [Activator]::CreateInstance($budgetType, $binding, $null, [object[]]@($limits), $null)
}
function Fixture([type]$type) {
    $value = [Activator]::CreateInstance($type)
    $i = 0
    foreach ($property in $type.GetProperties()) {
        if (!$property.CanWrite) { continue }
        $i++
        $t = $property.PropertyType
        if ($t -eq [string]) { $property.SetValue($value, " $($property.Name)`r`n$i " ) }
        elseif ($t -eq [bool]) { $property.SetValue($value, [bool]($i % 2)) }
        elseif ($t -eq [double]) { $property.SetValue($value, [double](-$i - 0.125)) }
        elseif ($t -eq [int]) { $property.SetValue($value, [int](-$i)) }
        elseif ($t -eq [DateTimeOffset]) { $property.SetValue($value, [DateTimeOffset]::Parse('2020-06-02T03:04:05.1234567+05:30')) }
        elseif ($t.IsEnum) { $values = [Enum]::GetValues($t); $property.SetValue($value, $values.GetValue($values.Length-1)) }
    }
    return $value
}
$excluded = @{
    Scope = @('Resources','VirtualFolders'); Folder = @('ChildNodeKeys'); Diagram = @('Objects','Workflows')
    Object = @('Metadata'); Workflow = @('Items'); Item = @('Queries'); Window = @('SpreadsheetFilters')
    Workspace = @('UnloadedResourceIds','OpenDocuments')
    Workbench = @('UnloadedResourceIds','OpenDocuments','ReferenceConnectionLines','ActiveDiagramSnapshot')
    Settings = @('CodeWindows','ReferenceHighlights','DiagramImages','KeyboardShortcuts','ResourceComparison','Diagnostics','Appearance')
}
foreach ($entry in $maps.GetFields([Reflection.BindingFlags]'Public,Static')) {
    $map = $entry.GetValue($null)
    $type = $map.GetType().GenericTypeArguments[0]
    $projection = $map.GetType().GetMethod('Projection', $binding).Invoke($map, [object[]]@('s'))
    $expectedProjection = foreach ($field in $map.Fields) {
        $expression = if ($field.Content) {
            "(SELECT c.[Text] FROM surf.TextContent c WHERE c.ContentKey=s.[$($field.Column)])"
        } else { "s.[$($field.Column)]" }
        if ($field.Type -eq [System.Data.SqlDbType]::NVarChar) {
            "CONVERT(bigint, DATALENGTH($expression)), $expression"
        } else { $expression }
    }
    Check ($projection -ceq ($expectedProjection -join ', ')) "$($entry.Name) length-before-LOB sequential projection"
    $source = Fixture $type
    if ($entry.Name -eq 'Object') {
        $source.Metadata.Link = " link`r`n "
        $source.Metadata.DocumentationXaml = " <Xaml> exact </Xaml>`r`n "
    }
    if ($entry.Name -eq 'Settings') {
        foreach ($p in @('CodeWindows','KeyboardShortcuts','ResourceComparison','Diagnostics','Appearance')) {
            $source.$p = Fixture $source.$p.GetType()
        }
    }
    $copy = $map.GetType().GetMethod('Copy', $binding).Invoke($map, [object[]]@($source, (Budget)))
    foreach ($field in $map.Fields) {
        $expected = $field.Get.Invoke($source)
        $actual = $field.Get.Invoke($copy)
        Check ($expected -ceq $actual) "$($entry.Name).$($field.Property) exact scalar round trip"
    }
    $writable = $type.GetProperties() | Where-Object { $_.CanWrite -and $_.Name -notin $excluded[$entry.Name] }
    foreach ($property in $writable) {
        Check ($property.Name -in $map.Fields.Property) "$($entry.Name).$($property.Name) mapped"
    }
}
$settingsMap = $maps.GetField('Settings').GetValue($null)
$keyboardType = $assembly.GetType('Surf2.Models.KeyboardShortcutSettings', $true)
foreach ($p in $keyboardType.GetProperties() | Where-Object { $_.CanWrite }) {
    $name = if ($p.Name -eq 'Version') { 'KeyboardShortcuts.Version' } else { $p.Name }
    Check ($name -in $settingsMap.Fields.Property) "Input flag $($p.Name) mapped"
}
$images = $assembly.GetType($prefix + 'StateImages', $true)
$resolver = $images.GetMethod('ResolveLeaf', $binding)
foreach ($name in @('../outside.png','..\outside.png','C:\outside.png','file.png:stream','image.png.','image.jpg')) {
    $failed = $false
    try { $null = $resolver.Invoke($null, [object[]]@('C:\unused\PastedDiagramImages', $name)) }
    catch { $failed = $_.Exception.InnerException -is [IO.InvalidDataException] }
    Check $failed "Rejected unsafe pasted-image name: $name"
}
$copyType = $assembly.GetType($prefix + 'StateCopies', $true)
$scopeType = $assembly.GetType('Surf2.Models.Scope', $true)
$folderType = $assembly.GetType('Surf2.Models.VirtualFolder', $true)
$scope = [Activator]::CreateInstance($scopeType)
$folder = [Activator]::CreateInstance($folderType)
$folder.ChildNodeKeys.Add('same')
$folder.ChildNodeKeys.Add('same')
$folder.ChildNodeKeys.Add('last')
$scope.VirtualFolders.Add($folder)
$scopeCopy = $copyType.GetMethod('Scope').Invoke($null, [object[]]@($scope, (Budget)))
Check (($scopeCopy.VirtualFolders[0].ChildNodeKeys -join '|') -ceq 'same|same|last') 'Duplicate ordered folder members preserved'
$scope.VirtualFolders[0].ChildNodeKeys.Clear()
Check ($scopeCopy.VirtualFolders[0].ChildNodeKeys.Count -eq 3) 'Captured graph independent from subsequent UI mutation'
$windowType = $assembly.GetType('Surf2.Models.OpenDocumentState', $true)
$window = [Activator]::CreateInstance($windowType)
$window.FontSize = 18.375
$window.SpreadsheetFilters.Add(9, 'first')
$window.SpreadsheetFilters.Add(-2, 'second')
$copy = $copyType.GetMethod('Window').Invoke($null, [object[]]@($window, (Budget)))
Check ($copy.FontSize -eq 18.375) 'Visible font size preserved'
Check (($copy.SpreadsheetFilters.Keys -join '|') -eq '9|-2') 'Column-index filter insertion order preserved'
$window.Width = [double]::NaN
$failed = $false
try { $null = $copyType.GetMethod('Window').Invoke($null, [object[]]@($window, (Budget))) }
catch { $failed = $_.Exception.InnerException -is [IO.InvalidDataException] }
Check $failed 'Nonfinite SQL float rejected explicitly'
$ddl = Get-Content -Raw (Join-Path $PSScriptRoot '../Schema/001.State.sql')
$rowMapSource = Get-Content -Raw (Join-Path $PSScriptRoot 'StateRowMap.cs')
Check ($rowMapSource -notmatch '\.GetChars\(') 'No consuming LOB length probe before text streaming'
Check ($ddl -notmatch '(?im)^\s*GO\s*$') 'DDL is one command without GO'
Check ($ddl -notmatch '(?i)PayloadJson|LibraryJson|nvarchar\(max\).*json') 'No library JSON storage'
Check ($ddl.Contains('FOREIGN KEY (WorkbenchKey, EmbeddedDiagramRevisionKey)')) 'Embedded diagram owner FK is explicit'
foreach ($entry in $maps.GetFields([Reflection.BindingFlags]'Public,Static')) {
    $map = $entry.GetValue($null)
    foreach ($field in $map.Fields) {
        Check ($ddl -match ('(?m)^\s*\[?' + [regex]::Escape($field.Column) + '\]?\s+')) "$($map.Table).$($field.Column) DDL destination exists"
    }
}
Write-Output "Passed $script:passed State contract checks. No SQL connection was opened."
