param([switch]$SkipBuild)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectFile = Join-Path $projectRoot 'src/Launcher.App/Launcher.App.csproj'
[xml]$project = Get-Content -LiteralPath $projectFile -Raw
$version = [string]($project.Project.PropertyGroup | Where-Object Version | Select-Object -First 1).Version
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[A-Za-z0-9.-]+)?$') { throw '项目发布版本无效' }
$bin = Join-Path $projectRoot 'bin'
[IO.Directory]::CreateDirectory($bin) | Out-Null
$publishRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts/publish'))
$temporary = [IO.Path]::GetFullPath((Join-Path $publishRoot ([Guid]::NewGuid().ToString('N'))))
$variants = @(
    @{Profile='SingleFile'; Contained='true'; Name="NovainaLauncher-$version-win-x64.exe"; Folder='full'},
    @{Profile='SingleFileLite'; Contained='false'; Name="NovainaLauncher-$version-win-x64-lite.exe"; Folder='lite'}
)

Push-Location $projectRoot
try {
    if (-not $SkipBuild) {
        # Build both variants before replacing either current release.
        foreach ($variant in $variants) {
            $output = Join-Path $temporary $variant.Folder
            & dotnet publish $projectFile -c Release -r win-x64 --self-contained $variant.Contained "-p:PublishProfile=$($variant.Profile)" -p:DebugType=none -p:DebugSymbols=false -o $output -v:minimal
            if ($LASTEXITCODE -ne 0) { throw "发布失败：$($variant.Profile)" }
            $files = @(Get-ChildItem -LiteralPath $output -Recurse -File)
            if ($files.Count -ne 1 -or $files[0].Name -ne 'NovainaLauncher.exe') { throw "单文件发布包含额外文件：$output" }
        }
        foreach ($variant in $variants) {
            Copy-Item -LiteralPath (Join-Path $temporary ($variant.Folder + '/NovainaLauncher.exe')) -Destination (Join-Path $bin $variant.Name) -Force
        }
    }
    $python = Get-Command py -ErrorAction SilentlyContinue
    if ($python) { & $python.Source -3 (Join-Path $PSScriptRoot 'package_source.py') }
    else { & python (Join-Path $PSScriptRoot 'package_source.py') }
    if ($LASTEXITCODE -ne 0) { throw '源码打包失败' }

    # Only launcher release files are touched; data and other local output remain intact.
    $currentNames = @($variants.Name) + "NovainaLauncher-source-$version.zip"
    foreach ($file in Get-ChildItem -LiteralPath $bin -File) {
        if ($file.Name -in $currentNames) { continue }
        if ($file.Name -match '^NovainaLauncher-source-\d+\.\d+\.\d+.*\.zip$') {
            $history = Join-Path $bin 'source-history'
            [IO.Directory]::CreateDirectory($history) | Out-Null
            $target = Join-Path $history $file.Name
            if (Test-Path -LiteralPath $target) { throw "历史源码已存在，未覆盖：$target" }
            Move-Item -LiteralPath $file.FullName -Destination $target
        }
        elseif ($file.Name -match '^NovainaLauncher-\d+\.\d+\.\d+.*-win-x64(?:-lite)?\.(?:exe|zip)$') {
            # An immediate file under the resolved bin directory, never recursive.
            if ([IO.Path]::GetDirectoryName($file.FullName) -ne $bin) { throw '发布清理路径越界' }
            Remove-Item -LiteralPath $file.FullName
        }
    }
    Write-Host "发布完成：$bin"
}
finally {
    Pop-Location
    if (Test-Path -LiteralPath $temporary) {
        $resolved = (Resolve-Path -LiteralPath $temporary).Path
        if (-not $resolved.StartsWith($publishRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw '临时发布清理路径越界' }
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}
