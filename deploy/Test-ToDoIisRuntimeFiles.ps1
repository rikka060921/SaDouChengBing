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
Test-Case 'nine nested roots restore directly without creating intermediate document copies' {
    $release=Join-Path $case 'eight\release';$root=$release+'\App_Data';$nested=$root
    for($i=0;$i -lt 9;$i++) { $nested += '\App_Data' }
    $name=('f' * 170) + '.docx'
    $source=$nested+'\ProjectDocuments\19\'+$name
    $seed=Join-Path $case 'eight\seed.txt';Write-Fixture $seed 'deep-original'
    [ToDoRuntime.NativeFiles]::CreateDirectory([ToDoRuntime.NativeFiles]::Parent($source))
    [ToDoRuntime.NativeFiles]::Copy($seed,$source)
    Check ($source.Length -gt 260) 'Fixture did not exceed MAX_PATH'
    $result=Repair-ToDoRuntimeNesting -ReleasePath $release
    $canonical=$root+'\ProjectDocuments\19\'+$name
    Check ($result.NestedDirectories -eq 9 -and $result.Copied -eq 1) 'Recovery recopied nested branches'
    Check ([ToDoRuntime.NativeFiles]::Hash($canonical) -eq [ToDoRuntime.NativeFiles]::Hash($source)) 'Long original was not restored faithfully'
    $cursor=$root
    for($i=1;$i -lt 9;$i++) {
        $cursor += '\App_Data'
        Check (![ToDoRuntime.NativeFiles]::Exists($cursor+'\ProjectDocuments')) 'Recovery created intermediate duplicate files'
    }
    $again=Repair-ToDoRuntimeNesting -ReleasePath $release
    Check ($again.Copied -eq 0 -and $again.Identical -eq 1) 'Long-path recovery is not idempotent'
}
Test-Case 'long source and destination paths support Unicode, overwrite and hash verification' {
    $source=Join-Path $case 'nine\old';$target=Join-Path $case 'nine\new'
    $relative=('segment-' + ('a' * 62) + '\') * 4 + ([string][char]0x7EAA) + ('b' * 210) + '.json'
    $file=$source+'\'+$relative;$destination=$target+'\'+$relative
    $seed=Join-Path $case 'nine\seed.txt';Write-Fixture $seed 'long-original'
    [ToDoRuntime.NativeFiles]::CreateDirectory([ToDoRuntime.NativeFiles]::Parent($file))
    [ToDoRuntime.NativeFiles]::Copy($seed,$file)
    Check ($file.Length -gt 500 -and $destination.Length -gt 500) 'Long-path fixtures are too short'
    $result=Copy-ToDoRuntimeDirectory $source $target
    Check ($result.Copied -eq 1) 'Long-path copy failed'
    Check ([ToDoRuntime.NativeFiles]::Hash($file) -eq [ToDoRuntime.NativeFiles]::Hash($destination)) 'Long-path hash mismatch'
    $replacement=Join-Path $case 'nine\replacement.txt';Write-Fixture $replacement 'updated-source'
    [ToDoRuntime.NativeFiles]::Move($replacement,$file)
    $result=Copy-ToDoRuntimeDirectory $source $target
    Check ($result.Copied -eq 1) 'Long-path overwrite failed'
    Check ([ToDoRuntime.NativeFiles]::Hash($file) -eq [ToDoRuntime.NativeFiles]::Hash($destination)) 'Long-path overwrite hash mismatch'
    $leftovers=@([ToDoRuntime.NativeFiles]::Children([ToDoRuntime.NativeFiles]::Parent($destination)) | Where-Object { $_.Name -like '__todo-*.tmp' })
    Check ($leftovers.Count -eq 0) 'Managed temporary files were left behind'
}
Test-Case 'excluded duplicate-root junction is rejected rather than traversed or hidden' {
    $source=Join-Path $case 'ten\old';$target=Join-Path $case 'ten\new';$outside=Join-Path $case 'ten\outside'
    Write-Fixture (Join-Path $outside 'private.txt') 'must-not-copy'
    New-Item -ItemType Directory -Path $source -Force | Out-Null
    New-Item -ItemType Junction -Path (Join-Path $source 'App_Data') -Target $outside | Out-Null
    $failed=$false
    try { Copy-ToDoRuntimeDirectory $source $target -ExcludeRootDirectoryName 'App_Data' | Out-Null } catch { $failed=$true }
    Check $failed 'Excluded junction was accepted'
    Check (!(Test-Path -LiteralPath $target)) 'Rejected linked source wrote a destination'
}
Test-Case 'destination junction is rejected before writing outside the destination' {
    $source=Join-Path $case 'eleven\old';$target=Join-Path $case 'eleven\new';$outside=Join-Path $case 'eleven\outside'
    Write-Fixture (Join-Path $source 'linked\new.txt') 'must-not-write'
    New-Item -ItemType Directory -Path $target,$outside -Force | Out-Null
    New-Item -ItemType Junction -Path (Join-Path $target 'linked') -Target $outside | Out-Null
    $failed=$false
    try { Copy-ToDoRuntimeDirectory $source $target | Out-Null } catch { $failed=$true }
    Check $failed 'Destination junction was accepted'
    Check (!(Test-Path -LiteralPath (Join-Path $outside 'new.txt'))) 'Linked external destination was written'
}
Test-Case 'atomic no-replace publication preserves a file created concurrently' {
    $target=Join-Path $case 'twelve\current.txt'
    $temporary=Join-Path $case ('twelve\__todo-'+[guid]::NewGuid().ToString('N')+'.tmp')
    Write-Fixture $temporary 'older-recovery-content'
    Write-Fixture $target 'concurrent-upload'
    $before=[ToDoRuntime.NativeFiles]::Hash($target);$failed=$false
    try { [ToDoRuntime.NativeFiles]::Move($temporary,$target,$false) } catch {$failed=$true}
    Check $failed 'No-replace publication overwrote an existing file'
    Check ([ToDoRuntime.NativeFiles]::Hash($target) -eq $before) 'Concurrent file was changed'
    Check ([ToDoRuntime.NativeFiles]::IsFile($temporary)) 'Failed publication removed the recoverable temporary file'
    [ToDoRuntime.NativeFiles]::DeleteTemporary($temporary)
}
foreach($path in @('ToDoRuntimeData.ps1','Repair-ToDoIisRuntimeFiles.ps1','Deploy-ToDo-Iis.ps1','Test-ToDoIisRuntimeFiles.ps1')) {
    $tokens=$null;$errors=$null
    [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $path),[ref]$tokens,[ref]$errors) | Out-Null
    Check ($errors.Count -eq 0) "PowerShell syntax error: $path"
}
Write-Host "RUNTIME TESTS PASSED: $script:Passed"
Write-Host "Fixtures preserved at: $case"
