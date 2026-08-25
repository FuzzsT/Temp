param(
    [ValidateSet('x64','x86')][string]$Arch='x64',
    [switch]$Publish
)
$ErrorActionPreference='Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host '[1/3] Building native hook + injector...'
& "$root\native\build-native.ps1" -Arch $Arch

if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet SDK 8 is required. Install Microsoft .NET 8 SDK, then rerun build.ps1.'
}

Write-Host '[2/3] Building Cube7Bridge...'
$proj = "$root\src\Cube7Bridge\Cube7Bridge.csproj"
if ($Publish) {
    dotnet publish $proj -c Release -r "win-$Arch" --self-contained true -p:PublishSingleFile=true -o "$root\bin"
} else {
    dotnet build $proj -c Release
    dotnet publish $proj -c Release -r "win-$Arch" --self-contained true -p:PublishSingleFile=true -o "$root\bin"
}
if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed.' }

Copy-Item "$root\config.json" "$root\bin\config.json" -Force
Write-Host '[3/3] Done.'
Write-Host "Run: $root\bin\Cube7Bridge.exe --mode=full"
