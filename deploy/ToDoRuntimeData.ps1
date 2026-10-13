Set-StrictMode -Version Latest

function Assert-ToDoRuntimeDirectory {
    param([string]$Path)
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Linked runtime paths are not supported: $cursor"
            }
        }
        $parent = [IO.Path]::GetDirectoryName($cursor)
        if ($parent -eq $cursor) { break }
        $cursor = $parent
    }
}

function Copy-ToDoRuntimeDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [ValidateSet('SourceWins','KeepExisting')][string]$ExistingFilePolicy = 'SourceWins'
    )
    $sourceRoot = [IO.Path]::GetFullPath($Source).TrimEnd([char[]]@('\','/'))
    $destinationRoot = [IO.Path]::GetFullPath($Destination).TrimEnd([char[]]@('\','/'))
    $separator = [IO.Path]::DirectorySeparatorChar
    $sourcePrefix = $sourceRoot + $separator
    $destinationPrefix = $destinationRoot + $separator
    if ($destinationRoot -eq $sourceRoot -or $destinationRoot.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Runtime destination cannot be the source directory or its child.'
    }
    Assert-ToDoRuntimeDirectory $sourceRoot
    Assert-ToDoRuntimeDirectory $destinationRoot
    if (!(Test-Path -LiteralPath $sourceRoot -PathType Container)) { throw "Source directory is missing: $sourceRoot" }
    $items = [Collections.Generic.List[IO.FileInfo]]::new()
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($sourceRoot)
    while ($pending.Count) {
        foreach ($item in @(Get-ChildItem -LiteralPath $pending.Pop() -Force -ErrorAction Stop)) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'Runtime source contains linked files or directories.'
            }
            if ($item.PSIsContainer) { $pending.Push($item.FullName) }
            else { $items.Add($item) }
        }
    }
    New-Item -ItemType Directory -Path $destinationRoot -Force | Out-Null
    $copied = 0; $identical = 0; $preserved = 0
    foreach ($file in $items) {
        $sourceFile = [IO.Path]::GetFullPath($file.FullName)
        if (!$sourceFile.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Runtime source escaped its root.' }
        Assert-ToDoRuntimeDirectory $sourceFile
        $relative = $sourceFile.Substring($sourcePrefix.Length)
        $target = [IO.Path]::GetFullPath((Join-Path $destinationRoot $relative))
        if (!$target.StartsWith($destinationPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Runtime destination escaped its root.' }
        Assert-ToDoRuntimeDirectory $target
        if (Test-Path -LiteralPath $target -PathType Container) { throw "Runtime file destination is a directory: $relative" }
        $sourceHash = (Get-FileHash -LiteralPath $sourceFile -Algorithm SHA256 -ErrorAction Stop).Hash
        if (Test-Path -LiteralPath $target -PathType Leaf) {
            $targetHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256 -ErrorAction Stop).Hash
            if ($sourceHash -eq $targetHash) { $identical++; continue }
            if ($ExistingFilePolicy -eq 'KeepExisting') {
                $preserved++
                Write-Warning "Preserved a different existing runtime file; review both copies: $relative"
                continue
            }
        }
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
        $temporary = $target + '.runtime-copy-' + [guid]::NewGuid().ToString('N') + '.tmp'
        try {
            Copy-Item -LiteralPath $sourceFile -Destination $temporary -ErrorAction Stop
            if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $sourceHash) { throw "Runtime copy hash mismatch: $relative" }
            Move-Item -LiteralPath $temporary -Destination $target -Force -ErrorAction Stop
            $copied++
        }
        finally {
            if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -ErrorAction Stop }
        }
    }
    [pscustomobject]@{Copied=$copied;Identical=$identical;PreservedDifferent=$preserved}
}

function Repair-ToDoRuntimeNesting {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][string]$ReleasePath)
    $release = [IO.Path]::GetFullPath($ReleasePath)
    Assert-ToDoRuntimeDirectory $release
    $copied = 0; $identical = 0; $preserved = 0; $nestedDirectories = 0
    foreach ($relative in @('App_Data', 'wwwroot\uploads')) {
        $root = Join-Path $release $relative
        if (!(Test-Path -LiteralPath $root -PathType Container)) { continue }
        $name = Split-Path $root -Leaf
        $candidate = Join-Path $root $name
        for ($depth=1; $depth -le 16 -and (Test-Path -LiteralPath $candidate -PathType Container); $depth++) {
            $nestedDirectories++
            Assert-ToDoRuntimeDirectory $candidate
            if ($PSCmdlet.ShouldProcess($root, "Copy only missing files from nested runtime directory $candidate")) {
                $result = Copy-ToDoRuntimeDirectory -Source $candidate -Destination $root -ExistingFilePolicy KeepExisting
                $copied += $result.Copied; $identical += $result.Identical; $preserved += $result.PreservedDifferent
            }
            $candidate = Join-Path $candidate $name
        }
        if (Test-Path -LiteralPath $candidate -PathType Container) { throw 'Runtime nesting exceeds the recovery depth limit; no directories were deleted.' }
    }
    [pscustomobject]@{NestedDirectories=$nestedDirectories;Copied=$copied;Identical=$identical;PreservedDifferent=$preserved}
}
