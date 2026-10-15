param([string]$PayloadZip = (Join-Path $PSScriptRoot '../release/Dolby-Access-PHRTF-Enabler-v2.0.0-win-x64.zip'), [switch]$UseEnvironmentToolchain)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $PayloadZip)) {
    throw 'Download the v2.0.0 Windows ZIP into release first, or supply -PayloadZip.'
}
Add-Type -AssemblyName System.IO.Compression
$archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $PayloadZip).Path)
try {
    $lines = foreach ($entry in $archive.Entries) {
        if ($entry.FullName.EndsWith('/')) { continue }
        if (-not $entry.FullName.StartsWith('DolbyAccessPhrtfEnabler/')) { throw 'Unexpected application ZIP root.' }
        $stream = $entry.Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
        finally { $stream.Dispose() }
        $hash + "`t" + $entry.Length + "`t" + $entry.FullName.Substring('DolbyAccessPhrtfEnabler/'.Length)
    }
    [IO.File]::WriteAllText((Join-Path $PSScriptRoot 'payload-manifest.txt'),($lines -join "`n")+"`n",[Text.UTF8Encoding]::new($false))
} finally { $archive.Dispose() }
$arguments = @('publish',(Join-Path $PSScriptRoot 'PortableLauncher.csproj'),'-c','Release','-o',(Join-Path $PSScriptRoot '../release/portable'),"-p:PayloadZip=$((Resolve-Path -LiteralPath $PayloadZip).Path)")
if ($UseEnvironmentToolchain) { $arguments += '-p:IlcUseEnvironmentalTools=true' }
dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw 'Portable launcher publish failed.' }
