param(
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"

$dotnetCliHome = Join-Path $env:TEMP "enfoque-dotnet"
New-Item -ItemType Directory -Path $dotnetCliHome -Force | Out-Null
$env:DOTNET_CLI_HOME = $dotnetCliHome

$project = Join-Path $PSScriptRoot "Enfoque\Enfoque.csproj"
$publishDir = Join-Path $PSScriptRoot "artifacts\publish\win-x64"
$installerScript = Join-Path $PSScriptRoot "installer\Enfoque.iss"
$isccCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe")
)

$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "No se encontró Inno Setup. Instálalo antes de generar el instalador."
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "La versión debe tener el formato mayor.menor.parche, por ejemplo 1.0.1."
}

$assemblyVersion = "$Version.0"

if (Test-Path $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}

dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $publishDir `
    /p:Version=$Version `
    /p:AssemblyVersion=$assemblyVersion `
    /p:FileVersion=$assemblyVersion `
    /p:InformationalVersion=$Version

$quotedVersion = '"' + $Version + '"'
& $iscc "/DAppVersion=$quotedVersion" $installerScript

$installerPath = Join-Path $PSScriptRoot "artifacts\installer\Enfoque-Setup-$Version.exe"
Write-Host "Instalador generado: $installerPath"
