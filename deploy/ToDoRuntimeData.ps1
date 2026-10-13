Set-StrictMode -Version Latest
if (!('ToDoRuntime.NativeFiles' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'ToDoRuntimeNative.cs') -ErrorAction Stop
}

function Assert-ToDoRuntimeDirectory {
    param([string]$Path)
    [ToDoRuntime.NativeFiles]::AssertNoReparse($Path)
}

function Copy-ToDoRuntimeDirectory {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination,
        [ValidateSet('SourceWins','KeepExisting')][string]$ExistingFilePolicy = 'SourceWins',
        [string]$ExcludeRootDirectoryName
    )
    $sourceRoot = [ToDoRuntime.NativeFiles]::FullPath($Source).TrimEnd([char[]]@('\','/'))
    $destinationRoot = [ToDoRuntime.NativeFiles]::FullPath($Destination).TrimEnd([char[]]@('\','/'))
    $separator = [IO.Path]::DirectorySeparatorChar
    $sourcePrefix = $sourceRoot + $separator
    $destinationPrefix = $destinationRoot + $separator
    if ($destinationRoot -eq $sourceRoot -or $destinationRoot.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Runtime destination cannot be the source directory or its child.'
    }
    Assert-ToDoRuntimeDirectory $sourceRoot
    Assert-ToDoRuntimeDirectory $destinationRoot
    if (![ToDoRuntime.NativeFiles]::IsDirectory($sourceRoot)) { throw "Source directory is missing: $sourceRoot" }
    $items = [Collections.Generic.List[string]]::new()
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($sourceRoot)
    while ($pending.Count) {
        $directory = $pending.Pop()
        foreach ($item in [ToDoRuntime.NativeFiles]::Children($directory)) {
            if ($item.IsReparsePoint) {
                throw 'Runtime source contains linked files or directories.'
            }
            if ($item.IsDirectory) {
                # Recovery handles each duplicate root level separately. Never copy
                # the next App_Data/uploads branch back into the canonical root.
                if ($directory -eq $sourceRoot -and $item.Name -eq $ExcludeRootDirectoryName) { continue }
                $pending.Push($item.FullName)
            }
            else { $items.Add($item.FullName) }
        }
    }
    [ToDoRuntime.NativeFiles]::CreateDirectory($destinationRoot)
    $copied = 0; $identical = 0; $preserved = 0
    foreach ($file in $items) {
        $sourceFile = [ToDoRuntime.NativeFiles]::FullPath($file)
        if (!$sourceFile.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Runtime source escaped its root.' }
        Assert-ToDoRuntimeDirectory $sourceFile
        $relative = $sourceFile.Substring($sourcePrefix.Length)
        $target = [ToDoRuntime.NativeFiles]::FullPath($destinationPrefix + $relative)
        if (!$target.StartsWith($destinationPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Runtime destination escaped its root.' }
        Assert-ToDoRuntimeDirectory $target
        if ([ToDoRuntime.NativeFiles]::IsDirectory($target)) { throw "Runtime file destination is a directory: $relative" }
        $sourceHash = [ToDoRuntime.NativeFiles]::Hash($sourceFile)
        if ([ToDoRuntime.NativeFiles]::IsFile($target)) {
            $targetHash = [ToDoRuntime.NativeFiles]::Hash($target)
            if ($sourceHash -eq $targetHash) { $identical++; continue }
            if ($ExistingFilePolicy -eq 'KeepExisting') {
                $preserved++
                Write-Warning "Preserved a different existing runtime file; review both copies: $relative"
                continue
            }
        }
        $parent = [ToDoRuntime.NativeFiles]::Parent($target)
        [ToDoRuntime.NativeFiles]::CreateDirectory($parent)
        $temporary = $parent + '\__todo-' + [guid]::NewGuid().ToString('N') + '.tmp'
        try {
            [ToDoRuntime.NativeFiles]::Copy($sourceFile, $temporary)
            if ([ToDoRuntime.NativeFiles]::Hash($temporary) -ne $sourceHash) { throw "Runtime copy hash mismatch: $relative" }
            try {
                [ToDoRuntime.NativeFiles]::Move($temporary, $target, $ExistingFilePolicy -eq 'SourceWins')
            }
            catch {
                # A live upload may create the canonical file while recovery copies.
                # The no-replace move protects it atomically, not just by an earlier check.
                if ($ExistingFilePolicy -ne 'KeepExisting' -or ![ToDoRuntime.NativeFiles]::IsFile($target)) { throw }
                Assert-ToDoRuntimeDirectory $target
                if ([ToDoRuntime.NativeFiles]::Hash($target) -eq $sourceHash) { $identical++ }
                else {
                    $preserved++
                    Write-Warning "Preserved a concurrently created runtime file; review both copies: $relative"
                }
                continue
            }
            $copied++
        }
        finally {
            if ([ToDoRuntime.NativeFiles]::Exists($temporary)) { [ToDoRuntime.NativeFiles]::DeleteTemporary($temporary) }
        }
    }
    [pscustomobject]@{Copied=$copied;Identical=$identical;PreservedDifferent=$preserved}
}

function Repair-ToDoRuntimeNesting {
    [CmdletBinding(SupportsShouldProcess)]
    param([Parameter(Mandatory)][string]$ReleasePath)
    $release = [ToDoRuntime.NativeFiles]::FullPath($ReleasePath)
    Assert-ToDoRuntimeDirectory $release
    $copied = 0; $identical = 0; $preserved = 0; $nestedDirectories = 0
    foreach ($relative in @('App_Data', 'wwwroot\uploads')) {
        $root = Join-Path $release $relative
        if (![ToDoRuntime.NativeFiles]::IsDirectory($root)) { continue }
        $name = Split-Path $root -Leaf
        $candidate = Join-Path $root $name
        for ($depth=1; $depth -le 64 -and [ToDoRuntime.NativeFiles]::IsDirectory($candidate); $depth++) {
            $nestedDirectories++
            Assert-ToDoRuntimeDirectory $candidate
            if ($PSCmdlet.ShouldProcess($root, "Copy only missing files from nested runtime directory $candidate")) {
                $result = Copy-ToDoRuntimeDirectory -Source $candidate -Destination $root -ExistingFilePolicy KeepExisting -ExcludeRootDirectoryName $name
                $copied += $result.Copied; $identical += $result.Identical; $preserved += $result.PreservedDifferent
            }
            $candidate = $candidate + '\' + $name
        }
        if ([ToDoRuntime.NativeFiles]::IsDirectory($candidate)) { throw 'Runtime nesting exceeds the recovery depth limit; no directories were deleted.' }
    }
    [pscustomobject]@{NestedDirectories=$nestedDirectories;Copied=$copied;Identical=$identical;PreservedDifferent=$preserved}
}
