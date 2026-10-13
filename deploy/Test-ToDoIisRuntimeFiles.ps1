[CmdletBinding()]
param([string]$OutputRoot)
$ErrorActionPreference = 'Stop'
if (!$OutputRoot) { $OutputRoot = Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts' }
. (Join-Path $PSScriptRoot 'ToDoRuntimeData.ps1')
$case = Join-Path ([IO.Path]::GetFullPath($OutputRoot)) ('iis-runtime-tests-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $case -Force | Out-Null
$script:Passed = 0
$archiveName = ([string][char]0x7EAA) + [char]0x8981 + '.json'
function Check([bool]$Condition,[string]$Message) { if (!$Condition) { throw $Message } }
function Write-Fixture([string]$Path,[string]$Content) {
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($Path)) -Force | Out-Null
    $temp = "$Path.fixture.tmp"
    [IO.File]::WriteAllText($temp,$Content,[Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temp -Destination $Path -Force
}
function Test-Case([string]$Name,[scriptblock]$Body) {
    & $Body
    $script:Passed++
    Write-Host "PASS $Name"
}
Test-Case 'existing publish directory receives contents without another App_Data layer' {
    $source=Join-Path $case 'one\old\App_Data';$target=Join-Path $case 'one\new\App_Data'
    Write-Fixture (Join-Path $source "ProjectDocuments\20\$archiveName") 'original-archive'
    Write-Fixture (Join-Path $target 'published.txt') 'published-fixture'
    $result=Copy-ToDoRuntimeDirectory $source $target
    Check ($result.Copied -eq 1) 'Expected one copied file'
    Check (Test-Path -LiteralPath (Join-Path $target "ProjectDocuments\20\$archiveName")) 'Canonical archive path is missing'
    Check (!(Test-Path -LiteralPath (Join-Path $target 'App_Data'))) 'Another App_Data layer was created'
    Check (Test-Path -LiteralPath (Join-Path $source "ProjectDocuments\20\$archiveName")) 'Source file was removed'
}
Test-Case 'multiple populated subdirectories do not gain nested duplicate names' {
    $source=Join-Path $case 'two\old\uploads';$target=Join-Path $case 'two\new\uploads'
    Write-Fixture (Join-Path $source 'projects\14\one.txt') 'old-runtime'
    Write-Fixture (Join-Path $target 'projects\14\one.txt') 'published-default'
    $result=Copy-ToDoRuntimeDirectory $source $target
    Check ((Get-Content -LiteralPath (Join-Path $target 'projects\14\one.txt') -Raw) -eq 'old-runtime') 'Runtime source did not win over publish defaults'
    Check (!(Test-Path -LiteralPath (Join-Path $target 'projects\projects'))) 'Subdirectory nesting was introduced'
}
Test-Case 'repeated content copy is idempotent' {
    $result=Copy-ToDoRuntimeDirectory (Join-Path $case 'one\old\App_Data') (Join-Path $case 'one\new\App_Data')
    Check ($result.Copied -eq 0 -and $result.Identical -eq 1) 'Repeated copy rewrote identical content'
}
Test-Case 'repair restores nested App_Data and uploads but preserves originals' {
    $release=Join-Path $case 'three\release'
    Write-Fixture (Join-Path $release 'App_Data\App_Data\ProjectDocuments\20\archive.json') 'archive-data'
    Write-Fixture (Join-Path $release 'App_Data\App_Data\App_Data\deep.txt') 'deep-data'
    Write-Fixture (Join-Path $release 'wwwroot\uploads\uploads\projects\14\file.txt') 'upload-data'
    $result=Repair-ToDoRuntimeNesting -ReleasePath $release
    Check ($result.NestedDirectories -eq 3) 'Nested directory count is unexpected'
    foreach($relative in @('App_Data\ProjectDocuments\20\archive.json','App_Data\deep.txt','wwwroot\uploads\projects\14\file.txt')) {
        Check (Test-Path -LiteralPath (Join-Path $release $relative)) "Missing restored file: $relative"
    }
    Check (Test-Path -LiteralPath (Join-Path $release 'App_Data\App_Data\ProjectDocuments\20\archive.json')) 'Repair deleted an original nested file'
}
Test-Case 'repair never overwrites a different existing canonical file' {
    $release=Join-Path $case 'four\release'
    Write-Fixture (Join-Path $release 'App_Data\cache.txt') 'current-content'
    Write-Fixture (Join-Path $release 'App_Data\App_Data\cache.txt') 'older-content'
    $result=Repair-ToDoRuntimeNesting -ReleasePath $release -WarningAction SilentlyContinue
    Check ($result.PreservedDifferent -eq 1) 'Conflicting file was not reported'
    Check ((Get-Content -LiteralPath (Join-Path $release 'App_Data\cache.txt') -Raw) -eq 'current-content') 'Canonical current file was overwritten'
}
Test-Case 'WhatIf recovery writes no files' {
    $release=Join-Path $case 'five\release'
    Write-Fixture (Join-Path $release 'App_Data\App_Data\one.txt') 'preview-only'
    $result=Repair-ToDoRuntimeNesting -ReleasePath $release -WhatIf
    Check (!(Test-Path -LiteralPath (Join-Path $release 'App_Data\one.txt'))) 'WhatIf wrote files'
}
Test-Case 'copy rejects identical source and destination roots' {
    $root=Join-Path $case 'one\old\App_Data';$failed=$false
    try { Copy-ToDoRuntimeDirectory $root $root | Out-Null } catch { $failed=$true }
    Check $failed 'Same-root copy was accepted'
}
Test-Case 'copy rejects a destination under the source root' {
    $root=Join-Path $case 'one\old\App_Data';$failed=$false
    try { Copy-ToDoRuntimeDirectory $root (Join-Path $root 'child') | Out-Null } catch { $failed=$true }
    Check $failed 'Recursive destination was accepted'
}
Test-Case 'directory at a file destination is rejected' {
    $source=Join-Path $case 'six\old';$target=Join-Path $case 'six\new'
    Write-Fixture (Join-Path $source 'same.txt') 'file-data'
    New-Item -ItemType Directory -Path (Join-Path $target 'same.txt') -Force | Out-Null
    $failed=$false
    try { Copy-ToDoRuntimeDirectory $source $target | Out-Null } catch { $failed=$true }
    Check $failed 'File-to-directory mismatch was accepted'
}
Test-Case 'linked source directories are rejected before traversal' {
    $source=Join-Path $case 'seven\old';$target=Join-Path $case 'seven\new';$outside=Join-Path $case 'seven\outside'
    Write-Fixture (Join-Path $outside 'private.txt') 'must-not-copy'
    New-Item -ItemType Directory -Path $source -Force | Out-Null
    New-Item -ItemType Junction -Path (Join-Path $source 'linked') -Target $outside | Out-Null
    $failed=$false
    try { Copy-ToDoRuntimeDirectory $source $target | Out-Null } catch { $failed=$true }
    Check $failed 'Linked source directory was accepted'
    Check (!(Test-Path -LiteralPath (Join-Path $target 'linked\private.txt'))) 'External linked content was copied'
}
foreach($path in @('ToDoRuntimeData.ps1','Repair-ToDoIisRuntimeFiles.ps1','Deploy-ToDo-Iis.ps1','Test-ToDoIisRuntimeFiles.ps1')) {
    $tokens=$null;$errors=$null
    [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $path),[ref]$tokens,[ref]$errors) | Out-Null
    Check ($errors.Count -eq 0) "PowerShell syntax error: $path"
}
Write-Host "RUNTIME TESTS PASSED: $script:Passed"
Write-Host "Fixtures preserved at: $case"
