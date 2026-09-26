# Builds the Docker image the FRUKT egg runs in (docker\Dockerfile). -Push uploads it; log in first with
#   docker login ghcr.io -u <github user>      (password: a GitHub token with write:packages)
# and make the package public on GitHub so Pterodactyl can pull it without logging in.
param(
    [string]$Image = "ghcr.io/phoenix557/frukt-server:latest",
    [switch]$Push
)

$ErrorActionPreference = "Stop"
$context = Join-Path $PSScriptRoot "docker"
foreach ($file in "Dockerfile", "entrypoint.sh") {
    $path = Join-Path $context $file
    $text = (Get-Content $path -Raw) -replace "`r`n", "`n"
    [IO.File]::WriteAllText($path, $text, (New-Object Text.UTF8Encoding $false))
}

$bin = Join-Path $context "bin"
dotnet publish (Join-Path $PSScriptRoot "FruktServer.csproj") -c Release -r linux-musl-x64 --self-contained `
    -p:PublishSingleFile=true -p:PublishTrimmed=true -p:DebugType=none -o $bin --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "FruktServer did not build." }

docker build -t $Image $context
if ($LASTEXITCODE -ne 0) { throw "The image did not build." }
if ($Push) {
    docker push $Image
    if ($LASTEXITCODE -ne 0) { throw "Pushing $Image failed. Run docker login ghcr.io first." }
}
Write-Host "Done: $Image"
