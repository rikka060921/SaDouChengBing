#requires -Version 7.0
<#
.SYNOPSIS
Validate the primary checkout and publish to the team repository and a history-isolated public mirror.
.DESCRIPTION
Run only after reviewing and committing changes. This is not an on-save uploader.
CheckOnly fetches and checks but never commits/pushes. SkipValidation is for an already
validated, unchanged HEAD (and local fixture tests), not ordinary delivery.
#>
[CmdletBinding()]
param(
    [string]$SourceDirectory = (Split-Path $PSScriptRoot -Parent),
    [string]$PublicDirectory,
    [string]$TeamRemote = 'origin',
    [string]$TeamBranch = 'Work-miracles(SaDouChengBing)',
    [string]$PublicRemote = 'origin',
    [string]$PublicBranch = 'main',
    [string]$Proxy,
    [switch]$CheckOnly,
    [switch]$SkipValidation,
    [switch]$ApproveConfigurationChanges
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$SourceDirectory = (Resolve-Path -LiteralPath $SourceDirectory).Path
if (!$PublicDirectory) { $PublicDirectory = Join-Path $SourceDirectory 'artifacts/github-export-20260923' }
$PublicDirectory = (Resolve-Path -LiteralPath $PublicDirectory).Path
if ($SourceDirectory -eq $PublicDirectory) { throw 'Source and public mirror must be separate checkouts.' }
if ($env:GIT_INDEX_FILE) { throw 'Unset GIT_INDEX_FILE before synchronization.' }

function Invoke-SyncGit([string]$Repo, [string[]]$Arguments) {
    $options = @('-c', 'core.quotepath=false')
    if ($Proxy) { $options += @('-c', "http.proxy=$Proxy") }
    $result = @(& git @options -C $Repo @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "Invoke-SyncGit failed ($LASTEXITCODE): $($Arguments[0]) in $Repo" }
    return ($result -join "`n")
}
function Assert-Clean([string]$Repo, [string]$Branch) {
    if ((Invoke-SyncGit $Repo @('rev-parse','--show-toplevel')).Replace('/','\') -ne $Repo.Replace('/','\')) {
        throw "Not a repository root: $Repo"
    }
    if ((Invoke-SyncGit $Repo @('branch','--show-current')) -ne $Branch) { throw "Wrong branch in $Repo; expected $Branch" }
    if (Invoke-SyncGit $Repo @('status','--porcelain','--untracked-files=all')) { throw "Uncommitted files in $Repo. Review and commit first." }
}
function Assert-Ancestor([string]$Repo, [string]$Before, [string]$After) {
    & git -C $Repo merge-base --is-ancestor $Before $After
    if ($LASTEXITCODE -ne 0) { throw 'Remote has changes missing from the source. Review and integrate first; no force push.' }
}
function Remote-Tip([string]$Repo, [string]$Remote, [string]$Branch) {
    $line = Invoke-SyncGit $Repo @('ls-remote','--exit-code',$Remote,"refs/heads/$Branch")
    return ($line -split '\s+')[0]
}

Assert-Clean $SourceDirectory $TeamBranch
Assert-Clean $PublicDirectory $PublicBranch
$sourceHead = Invoke-SyncGit $SourceDirectory @('rev-parse','HEAD')
# Pin the expected public tip locally. Never infer approval from a newly fetched public branch.
$approvedPublicBase = Invoke-SyncGit $SourceDirectory @('config','--get','todoSync.githubBase')
if ($approvedPublicBase -notmatch '^[a-f0-9]{40}$') { throw 'Missing reviewed todoSync.githubBase. See synchronization guide.' }

Invoke-SyncGit $SourceDirectory @('fetch','--no-tags',$TeamRemote,$TeamBranch) | Out-Null
$teamTip = Invoke-SyncGit $SourceDirectory @('rev-parse','FETCH_HEAD')
Assert-Ancestor $SourceDirectory $teamTip $sourceHead
Invoke-SyncGit $PublicDirectory @('fetch','--no-tags',$PublicRemote,$PublicBranch) | Out-Null
$publicTip = Invoke-SyncGit $PublicDirectory @('rev-parse','FETCH_HEAD')
$publicHead = Invoke-SyncGit $PublicDirectory @('rev-parse','HEAD')

# Import objects locally only. The public commit will have ONLY the public parent,
# never the team history (which may contain legacy private data).
Invoke-SyncGit $PublicDirectory @('fetch','--no-tags',$SourceDirectory,$sourceHead) | Out-Null
$gitDirectory = Invoke-SyncGit $PublicDirectory @('rev-parse','--absolute-git-dir')
$indexFile = Join-Path $gitDirectory ("todo-public-index-" + [guid]::NewGuid().ToString('N'))
try {
    $env:GIT_INDEX_FILE = $indexFile
    Invoke-SyncGit $PublicDirectory @('read-tree',$sourceHead) | Out-Null
    # Existing team meeting caches stay in the team repo, never in the public mirror.
    foreach ($path in @('ToDo.Razor/App_Data','ToDo.Razor/wwwroot/uploads','artifacts','.vs')) {
        Invoke-SyncGit $PublicDirectory @('rm','-r','--cached','--ignore-unmatch','--',$path) | Out-Null
    }
    $publicTree = Invoke-SyncGit $PublicDirectory @('write-tree')
} finally {
    Remove-Item Env:GIT_INDEX_FILE -ErrorAction SilentlyContinue
    # Only this uniquely named temporary index, never a worktree or the real index.
    if (Test-Path -LiteralPath $indexFile) { Remove-Item -LiteralPath $indexFile }
}
$paths = (Invoke-SyncGit $PublicDirectory @('ls-tree','-r','--name-only',$publicTree)) -split "`n"
$blocked = @($paths | Where-Object {
    $_ -match '(?i)(^|/)(\.env($|\.)|id_rsa$|id_ed25519$)' -or
    $_ -match '(?i)\.(pfx|p12|pem|key|db|sqlite|sqlite3|mdf|ldf|bak|bundle)$'
})
if ($blocked.Count) { throw "Potential private files in public tree: $($blocked -join ', '). Review exclusions before publishing." }

# Retry is allowed only for the exact snapshot previously prepared by this script.
$prepared = $false
if ($publicHead -ne $approvedPublicBase) {
    $parent = Invoke-SyncGit $PublicDirectory @('rev-list','--parents','-n','1',$publicHead)
    $message = Invoke-SyncGit $PublicDirectory @('log','-1','--format=%B',$publicHead)
    $tree = Invoke-SyncGit $PublicDirectory @('rev-parse',"$publicHead^{tree}")
    if ($parent -ne "$publicHead $approvedPublicBase" -or
        $tree -ne $publicTree -or $message -notmatch "(?m)^ToDo-Source: $sourceHead$") {
        throw 'Public checkout has unreviewed commits or an unfinished DIFFERENT snapshot. Resolve before synchronization.'
    }
    $prepared = $true
}
if ($publicTip -ne $approvedPublicBase -and !($prepared -and $publicTip -eq $publicHead)) {
    throw 'Public remote advanced independently. Review those changes; do not overwrite or blindly update githubBase.'
}
$configChanges = Invoke-SyncGit $PublicDirectory @('diff','--name-only',$approvedPublicBase,$publicTree,'--',
    ':(glob)**/appsettings*.json',':(glob)**/launchSettings.json',':(glob)**/NuGet.Config')
if ($configChanges -and !$ApproveConfigurationChanges) {
    throw "Configuration changes need explicit review for credentials before -ApproveConfigurationChanges: $configChanges"
}
Write-Host "Primary source: $SourceDirectory"
Write-Host "Committed source: $sourceHead"
Write-Host "Team remote tip: $teamTip"
Write-Host "Public remote tip: $publicTip"
Write-Host "Public snapshot tree: $publicTree"
if ($CheckOnly) {
    Write-Host 'Preflight passed. No commits, pushes, or builds performed.'
    return
}
if (!$SkipValidation) {
    Push-Location $SourceDirectory
    try {
        & dotnet test ./ToDo.Test/ToDo.Test.csproj -c Release --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Tests failed; not publishing.' }
        foreach ($configuration in @('Release','Debug')) {
            & dotnet build ./ToDo.sln -c $configuration --no-restore --nologo
            if ($LASTEXITCODE -ne 0) { throw "$configuration build failed; not publishing." }
        }
    } finally { Pop-Location }
}
Assert-Clean $SourceDirectory $TeamBranch
Assert-Clean $PublicDirectory $PublicBranch
if ((Invoke-SyncGit $SourceDirectory @('rev-parse','HEAD')) -ne $sourceHead -or
    (Invoke-SyncGit $PublicDirectory @('rev-parse','HEAD')) -ne $publicHead) { throw 'HEAD changed during validation. Start again.' }

if (!$prepared -and (Invoke-SyncGit $PublicDirectory @('rev-parse','HEAD^{tree}')) -ne $publicTree) {
    $title = Invoke-SyncGit $SourceDirectory @('log','-1','--format=%s')
    $sourceDescription = Invoke-SyncGit $SourceDirectory @('log','-1','--format=%b')
    $body = "$sourceDescription`n`nPublish validated primary checkout as a public snapshot. Preserve independent public history; exclude runtime data and raw meeting caches.`n`nToDo-Source: $sourceHead"
    $publicHead = Invoke-SyncGit $PublicDirectory @('commit-tree',$publicTree,'-p',$approvedPublicBase,'-m',$title,'-m',$body)
    # Normal fast-forward honors local working-tree safety checks.
    Invoke-SyncGit $PublicDirectory @('merge','--ff-only',$publicHead) | Out-Null
}
# Two remote pushes cannot be atomic. If either fails, rerun with the same HEAD.
Invoke-SyncGit $SourceDirectory @('push',$TeamRemote,"${sourceHead}:refs/heads/$TeamBranch") | Out-Null
if ((Remote-Tip $SourceDirectory $TeamRemote $TeamBranch) -ne $sourceHead) { throw 'Team push could not be verified.' }
Write-Host "TEAM VERIFIED: $sourceHead"
Invoke-SyncGit $PublicDirectory @('push',$PublicRemote,"${publicHead}:refs/heads/$PublicBranch") | Out-Null
if ((Remote-Tip $PublicDirectory $PublicRemote $PublicBranch) -ne $publicHead) { throw 'Public push could not be verified.' }
Invoke-SyncGit $SourceDirectory @('config','todoSync.githubBase',$publicHead) | Out-Null
Write-Host "GITHUB VERIFIED: $publicHead"
Write-Host 'Both remotes synchronized. Server deployment was not performed.'
