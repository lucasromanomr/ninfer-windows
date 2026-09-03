[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\dist\ninfer-control-portable-release'),
    [string]$ServerBuildDirectory = (Join-Path $PSScriptRoot '..\..\build-ninja'),
    [string]$VcpkgRoot = 'C:\src\vcpkg',
    [string]$CudaRoot = 'C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.3',
    [switch]$SkipServerBuild,
    [switch]$SkipZip
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'build-common.ps1')

$projectPath = Join-Path $PSScriptRoot 'NInferControl.csproj'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
$serverBuildPath = [System.IO.Path]::GetFullPath($ServerBuildDirectory)
$zipPath = Join-Path (Split-Path $outputPath -Parent) 'NInferControl-Portable-x64.zip'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK não encontrado.'
}

if ($SkipServerBuild) {
    Write-Host 'Compilação do servidor nativo ignorada (-SkipServerBuild).'
}
else {
    Write-Host "Compilando o servidor nativo em $serverBuildPath..."
    Build-NativeServer -RepositoryRoot $repositoryRoot -ServerBuildPath $serverBuildPath -VcpkgRoot $VcpkgRoot -CudaRoot $CudaRoot
}

$serverReleasePath = Resolve-ServerReleaseDirectory -ServerBuildPath $serverBuildPath

Assert-NInferControlNotRunning -Path $outputPath

if (Test-Path -LiteralPath $outputPath) {
    Remove-Item -LiteralPath $outputPath -Recurse -Force
}
New-Item -ItemType Directory -Path $outputPath | Out-Null

& dotnet publish $projectPath -c Release -p:Platform=x64 -r win-x64 --self-contained true `
    -p:WindowsPackageType=None `
    -p:EnableMsixTooling=true `
    -p:WindowsAppSDKSelfContained=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    "-o:$outputPath"
if ($LASTEXITCODE -ne 0) {
    throw "A publicação portátil falhou com código $LASTEXITCODE."
}

$executable = Join-Path $outputPath 'NInferControl.exe'
if (-not (Test-Path -LiteralPath $executable)) {
    throw 'O executável portátil não foi gerado.'
}

# ninfer-serve.exe sits next to NInferControl.exe: it is the first candidate probed by
# FindServerExecutable, so the app resolves the server without manual configuration.
$payload = Get-ChildItem -LiteralPath $serverReleasePath -File |
    Where-Object { $_.Extension -in '.exe', '.dll' }
foreach ($file in $payload) {
    Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $outputPath $file.Name) -Force
}
Write-Host "Servidor incluído: $($payload.Count) arquivo(s) de $serverReleasePath."

$readmePath = Join-Path $outputPath 'LEIA-ME.txt'
@'
NInfer Control - versão portátil

Não requer instalação, certificado, MSIX ou permissão de administrador.
Abra NInferControl.exe diretamente.

O ninfer-serve.exe acompanha o pacote, nesta mesma pasta, junto das DLLs de que
precisa. O app o encontra sozinho - não é preciso apontar o servidor na tela de
configuração. Mantenha os arquivos juntos ao mover a pasta.

Se o app continuar abrindo outro servidor, é porque há um caminho salvo de uma
execução anterior em %LOCALAPPDATA%\NInferControl\settings.json. Apague o campo
ServerPath ou aponte o executável desta pasta.

As configurações ficam em %LOCALAPPDATA%\NInferControl\settings.json.
'@ | Set-Content -LiteralPath $readmePath -Encoding utf8

if ($SkipZip) {
    Write-Host 'Compactação ignorada (-SkipZip).'
}
else {
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }
    Compress-Archive -Path (Join-Path $outputPath '*') -DestinationPath $zipPath -CompressionLevel Optimal
    Write-Host "ZIP: $zipPath"
}

Write-Host "Executável: $executable"
Write-Host "Servidor: $(Join-Path $outputPath 'ninfer-serve.exe')"
