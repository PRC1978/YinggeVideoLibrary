param(
    [string]$ArchivePath = (Join-Path $env:TEMP 'yingge-postgresql-18.6-5-windows-x64-binaries.zip'),
    [switch]$Download
)

$ErrorActionPreference = 'Stop'
$releaseUrl = 'https://sbp.enterprisedb.com/getfile.jsp?fileid=1260609'
$expectedSha256 = 'E2246BA91D22345BC3D017586C09EDE52D9DF180B1EEB480F050445F1CAD84E2'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifacts = [IO.Path]::GetFullPath((Join-Path $repo 'artifacts'))
$target = [IO.Path]::GetFullPath((Join-Path $artifacts 'postgres-runtime'))

if (-not (Test-Path -LiteralPath $ArchivePath)) {
    if (-not $Download) { throw 'PostgreSQL archive is missing. Pass -Download or -ArchivePath.' }
    $archiveParent = Split-Path -Parent ([IO.Path]::GetFullPath($ArchivePath))
    New-Item -ItemType Directory -Path $archiveParent -Force | Out-Null
    Invoke-WebRequest -Uri $releaseUrl -OutFile $ArchivePath
}
$actualSha256 = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash
if ($actualSha256 -ne $expectedSha256) {
    throw "PostgreSQL archive SHA-256 mismatch: $actualSha256"
}

New-Item -ItemType Directory -Path $artifacts -Force | Out-Null
if (Test-Path -LiteralPath $target) {
    $manifest = Join-Path $target 'bundle-manifest.json'
    if (-not (Test-Path -LiteralPath $manifest)) { throw 'Existing PostgreSQL runtime has no manifest; refusing to replace it.' }
    $existing = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
    if ($existing.ArchiveSha256 -ne $expectedSha256) { throw 'Existing PostgreSQL runtime has a different archive hash; refusing to replace it.' }
    Write-Output "Already staged: $target"
    exit 0
}

$stage = [IO.Path]::GetFullPath((Join-Path $artifacts ('.postgres-runtime-stage-' + [guid]::NewGuid().ToString('N'))))
$rootPrefix = $stage.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $rootPrefix.StartsWith($artifacts.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) { throw 'Stage escaped artifacts directory.' }

try {
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    Add-Type -AssemblyName System.IO.Compression
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            $include = $name.StartsWith('pgsql/bin/', [StringComparison]::Ordinal) -or
                $name.StartsWith('pgsql/lib/', [StringComparison]::Ordinal) -or
                $name.StartsWith('pgsql/share/', [StringComparison]::Ordinal) -or
                $name -in @('pgsql/server_license.txt', 'pgsql/commandlinetools_3rd_party_licenses.txt')
            if (-not $include -or $name.EndsWith('/')) { continue }
            $relative = $name.Substring('pgsql/'.Length).Replace('/', [IO.Path]::DirectorySeparatorChar)
            $output = [IO.Path]::GetFullPath((Join-Path $stage $relative))
            if (-not $output.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                throw "Archive entry escaped stage: $name"
            }
            New-Item -ItemType Directory -Path (Split-Path -Parent $output) -Force | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $output, $false)
        }
    }
    finally { $archive.Dispose() }

    foreach ($relative in @('bin\postgres.exe', 'bin\pg_ctl.exe', 'bin\initdb.exe',
            'bin\pg_dump.exe', 'bin\pg_restore.exe', 'share\postgres.bki', 'server_license.txt')) {
        if (-not (Test-Path -LiteralPath (Join-Path $stage $relative))) {
            throw "PostgreSQL bundle missing $relative"
        }
    }
    $version = & (Join-Path $stage 'bin\postgres.exe') --version
    if ($LASTEXITCODE -ne 0 -or $version -ne 'postgres (PostgreSQL) 18.6') {
        throw "Unexpected PostgreSQL version: $version"
    }
    [pscustomobject]@{
        Version = '18.6'
        Source = $releaseUrl
        ArchiveSha256 = $actualSha256
        PreparedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'bundle-manifest.json') -Encoding utf8
    if (Test-Path -LiteralPath $target) { throw 'Target appeared during staging; refusing to replace it.' }
    Move-Item -LiteralPath $stage -Destination $target
    Write-Output "Prepared PostgreSQL 18.6: $target"
}
finally {
    if (Test-Path -LiteralPath $stage) {
        if (-not $stage.StartsWith($artifacts.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar,
                [StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing cleanup outside artifacts.' }
        Remove-Item -LiteralPath $stage -Recurse -Force
    }
}
