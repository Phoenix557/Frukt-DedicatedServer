# Builds FruktServer as single files that need nothing installed, for running a server without Docker:
#   dist\FruktServer.exe   (Windows)      dist\FruktServer   (Linux, run with ./FruktServer --name "My Server")
$ErrorActionPreference = "Stop"
$project = Join-Path $PSScriptRoot "FruktServer.csproj"
$dist = Join-Path $PSScriptRoot "dist"
New-Item -ItemType Directory -Force $dist | Out-Null

foreach ($runtime in "win-x64", "linux-x64") {
    $out = Join-Path $PSScriptRoot "bin\publish\$runtime"
    dotnet publish $project -c Release -r $runtime --self-contained -p:PublishSingleFile=true -p:PublishTrimmed=true `
        -p:DebugType=none -o $out --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "FruktServer did not build for $runtime." }
    $name = if ($runtime -like "win-*") { "FruktServer.exe" } else { "FruktServer" }
    Copy-Item (Join-Path $out $name) (Join-Path $dist $name) -Force
}
Get-ChildItem $dist | ForEach-Object { "{0}  {1:N1} MB" -f $_.Name, ($_.Length / 1MB) }
