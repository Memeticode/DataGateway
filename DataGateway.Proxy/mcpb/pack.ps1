# Builds datagateway-proxy.mcpb, a Claude Desktop extension holding DataGateway.Proxy as one self-contained program,
# so a user's PC needs no .NET install. Run from the public source root:
#   pwsh -File DataGateway.Proxy/mcpb/pack.ps1 [-Out <folder>]
# A .mcpb file is a zip with manifest.json at its root and the program under server/.
param([string]$Out = 'publish/mcpb')
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$stage = Join-Path $Out 'stage'
if (Test-Path $stage) { throw "Stage folder $stage exists: choose another -Out or clear it first." }

dotnet publish (Join-Path $here '..' 'DataGateway.Proxy.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o (Join-Path $stage 'server')
if ($LASTEXITCODE -ne 0) { throw 'Publishing DataGateway.Proxy failed.' }
$program = Join-Path $stage 'server' 'DataGateway.Proxy.exe'
if (-not (Test-Path $program)) { throw "Publish produced no $program." }
Copy-Item (Join-Path $here 'manifest.json') $stage

# Entries are written by hand so that every name uses forward slashes, whatever the .NET version.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$package = [IO.Path]::GetFullPath((Join-Path $Out 'datagateway-proxy.mcpb'))
if (Test-Path $package) { throw "$package exists: choose another -Out or move it first." }
$zip = [IO.Compression.ZipFile]::Open($package, 'Create')
try {
    $root = (Resolve-Path $stage).Path.TrimEnd('\') + '\'
    foreach ($file in Get-ChildItem $stage -Recurse -File | Where-Object Extension -notin '.pdb', '.xml') {
        $name = $file.FullName.Substring($root.Length).Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $name)
    }
} finally { $zip.Dispose() }
"Built $package"
