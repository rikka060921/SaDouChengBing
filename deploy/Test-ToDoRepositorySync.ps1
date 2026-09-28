#requires -Version 7.0
[CmdletBinding()]
param([string]$OutputDirectory)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$syncScript = Join-Path $PSScriptRoot 'Sync-ToDoRepositories.ps1'
if (!$OutputDirectory) {
    $OutputDirectory = Join-Path (Split-Path $PSScriptRoot -Parent) ('artifacts/sync-tests-' + [guid]::NewGuid().ToString('N'))
}
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Use a new fixture directory.' }
New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
$OutputDirectory = (Resolve-Path -LiteralPath $OutputDirectory).Path
function G([string]$Repo, [string[]]$Arguments) {
    $result = @(& git -C $Repo @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "Fixture git failed: $($Arguments -join ' ')" }
    return ($result -join "`n")
}
function Write-Fixture([string]$Path, [string]$Content) {
    [IO.Directory]::CreateDirectory((Split-Path $Path -Parent)) | Out-Null
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}
$script:checks = 0
function Assert([bool]$Condition, [string]$Label) {
    if (!$Condition) { throw "FAIL: $Label" }
    $script:checks++
    Write-Host "PASS: $Label"
}
function Expect-Failure([scriptblock]$Action, [string]$Pattern, [string]$Label) {
    $errorMessage = ''
    try { & $Action } catch { $errorMessage = $_.Exception.Message }
    Assert ($errorMessage -match $Pattern) "$Label ($errorMessage)"
}
$source = Join-Path $OutputDirectory 'source'
$mirror = Join-Path $OutputDirectory 'public'
$team = Join-Path $OutputDirectory 'team.git'
$publicRemote = Join-Path $OutputDirectory 'public.git'
foreach ($repo in @($source,$mirror,$team,$publicRemote)) {
    New-Item -ItemType Directory -Path $repo | Out-Null
    if ($repo.EndsWith('.git')) { G $repo @('init','--bare','-b','main') | Out-Null }
    else {
        G $repo @('init','-b','main') | Out-Null
        G $repo @('config','user.name','Sync Fixture') | Out-Null
        G $repo @('config','user.email','sync-fixture@example.invalid') | Out-Null
    }
}
foreach ($repo in @($source,$mirror)) {
    Write-Fixture (Join-Path $repo 'code.txt') "version 1`n"
    Write-Fixture (Join-Path $repo 'ToDo.Razor/appsettings.json') "{}`n"
}
Write-Fixture (Join-Path $source 'ToDo.Razor/App_Data/TencentMeetCache/private.json') '{"private":true}'
G $source @('add','code.txt','ToDo.Razor') | Out-Null
G $source @('commit','-m','private team baseline') | Out-Null
$privateCommit = G $source @('rev-parse','HEAD')
G $source @('remote','add','origin',$team) | Out-Null
G $source @('push','origin','main') | Out-Null
G $mirror @('add','code.txt','ToDo.Razor') | Out-Null
G $mirror @('commit','-m','independent public baseline') | Out-Null
G $mirror @('remote','add','origin',$publicRemote) | Out-Null
G $mirror @('push','origin','main') | Out-Null
$base = G $mirror @('rev-parse','HEAD')
G $source @('config','todoSync.githubBase',$base) | Out-Null
$argsForSync = @{ SourceDirectory=$source; PublicDirectory=$mirror; TeamBranch='main'; SkipValidation=$true }
Write-Fixture (Join-Path $source 'code.txt') "version 2`n"
G $source @('add','code.txt') | Out-Null
G $source @('commit','-m','second version') | Out-Null
& $syncScript @argsForSync -CheckOnly
Assert ((G $publicRemote @('rev-parse','main')) -eq $base) 'CheckOnly does not publish'
& $syncScript @argsForSync
Assert ((G $team @('rev-parse','main')) -eq (G $source @('rev-parse','HEAD'))) 'team matches source'
Assert ((G $publicRemote @('show','main:code.txt')) -eq 'version 2') 'public gets latest code'
Assert (!(G $publicRemote @('ls-tree','-r','--name-only','main','ToDo.Razor/App_Data'))) 'private cache excluded'
Assert (!((G $publicRemote @('rev-list','main')) -split "`n" -contains $privateCommit)) 'team history not published'
$published = G $mirror @('rev-parse','HEAD')
& $syncScript @argsForSync
Assert ((G $mirror @('rev-parse','HEAD')) -eq $published) 'repeat does not create duplicate commits'
Write-Fixture (Join-Path $source 'unreviewed.txt') 'pending'
Expect-Failure { & $syncScript @argsForSync } 'Uncommitted' 'dirty source refuses'
Remove-Item -LiteralPath (Join-Path $source 'unreviewed.txt')
Write-Fixture (Join-Path $mirror 'unreviewed.txt') 'pending'
Expect-Failure { & $syncScript @argsForSync } 'Uncommitted' 'dirty mirror refuses'
Remove-Item -LiteralPath (Join-Path $mirror 'unreviewed.txt')
Write-Fixture (Join-Path $source 'ToDo.Razor/appsettings.json') '{"ExampleSetting":true}'
G $source @('add','ToDo.Razor/appsettings.json') | Out-Null
G $source @('commit','-m','configuration change') | Out-Null
Expect-Failure { & $syncScript @argsForSync } 'Configuration changes' 'configuration needs review'
& $syncScript @argsForSync -ApproveConfigurationChanges
Assert ((G $publicRemote @('show','main:ToDo.Razor/appsettings.json')) -eq '{"ExampleSetting":true}') 'reviewed configuration can publish'
Write-Fixture (Join-Path $source 'code.txt') "version 3`n"
G $source @('add','code.txt') | Out-Null
G $source @('commit','-m','third version') | Out-Null
$hook = Join-Path $publicRemote 'hooks/pre-receive'
Write-Fixture $hook "#!/bin/sh`nexit 1`n"
Expect-Failure { & $syncScript @argsForSync } 'failed' 'public rejection reports partial failure'
Assert ((G $team @('rev-parse','main')) -eq (G $source @('rev-parse','HEAD'))) 'team success is retained'
$prepared = G $mirror @('rev-parse','HEAD')
Remove-Item -LiteralPath $hook
& $syncScript @argsForSync
Assert ((G $publicRemote @('rev-parse','main')) -eq $prepared) 'retry reuses prepared snapshot'
# Another developer advances public main. Source must not overwrite it.
$other = Join-Path $OutputDirectory 'other'
& git clone $publicRemote $other
if ($LASTEXITCODE -ne 0) { throw 'Fixture clone failed' }
G $other @('config','user.name','Other Fixture') | Out-Null
G $other @('config','user.email','other@example.invalid') | Out-Null
Write-Fixture (Join-Path $other 'other.txt') 'other developer'
G $other @('add','other.txt') | Out-Null
G $other @('commit','-m','independent public change') | Out-Null
G $other @('push','origin','main') | Out-Null
Expect-Failure { & $syncScript @argsForSync } 'advanced independently' 'independent public edits refuse'
$otherTeam = Join-Path $OutputDirectory 'other-team'
& git clone $team $otherTeam
if ($LASTEXITCODE -ne 0) { throw 'Fixture team clone failed' }
G $otherTeam @('config','user.name','Other Fixture') | Out-Null
G $otherTeam @('config','user.email','other@example.invalid') | Out-Null
Write-Fixture (Join-Path $otherTeam 'other.txt') 'team developer'
G $otherTeam @('add','other.txt') | Out-Null
G $otherTeam @('commit','-m','independent team change') | Out-Null
G $otherTeam @('push','origin','main') | Out-Null
Expect-Failure { & $syncScript @argsForSync } 'Remote has changes' 'independent team edits refuse'
Write-Host "PASSED: $script:checks synchronization checks. Fixtures retained: $OutputDirectory"
