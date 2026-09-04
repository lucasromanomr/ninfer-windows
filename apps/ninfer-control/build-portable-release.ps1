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

# NInferServerDirectory carries the server binaries into the bundle, and
# IncludeAllContentForSelfExtract makes the runtime unpack them next to the managed assemblies,
# which is where AppContext.BaseDirectory points: the app resolves the server with nothing beside
# the executable.
& dotnet publish $projectPath -c Release -p:Platform=x64 -r win-x64 --self-contained true `
    -p:WindowsPackageType=None `
    -p:EnableMsixTooling=true `
    -p:WindowsAppSDKSelfContained=true `
    -p:IncludeAllContentForSelfExtract=true `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    "-p:NInferServerDirectory=$serverReleasePath" `
    "-o:$outputPath"
if ($LASTEXITCODE -ne 0) {
    throw "A publicação portátil falhou com código $LASTEXITCODE."
}

$executable = Join-Path $outputPath 'NInferControl.exe'
if (-not (Test-Path -LiteralPath $executable)) {
    throw 'O executável portátil não foi gerado.'
}

$stray = Get-ChildItem -LiteralPath $outputPath -File |
    Where-Object { $_.Name -in 'ninfer-serve.exe', 'ninfer.exe' }
if ($stray) {
    throw 'Os binarios do servidor ficaram soltos na pasta em vez de entrar no executavel.'
}
Write-Host "Servidor embutido no executável a partir de $serverReleasePath."

$readmePath = Join-Path $outputPath 'LEIA-ME.txt'
@'
NInfer Control - versão portátil

Não requer instalação, certificado, MSIX ou permissão de administrador.
Abra NInferControl.exe diretamente.

O ninfer-serve.exe vai embutido dentro do NInferControl.exe. Não há nada para
instalar, copiar junto ou apontar na tela de configuração: o app encontra o
servidor sozinho. O executável é o pacote inteiro - pode movê-lo para onde quiser.

Na primeira execução o Windows leva alguns segundos a mais para abrir, porque o
conteúdo embutido é descompactado em cache. As execuções seguintes reaproveitam
esse cache.

Se o app abrir outro servidor que não o embutido, é porque há um caminho salvo de
uma execução anterior em %LOCALAPPDATA%\NInferControl\settings.json. Apague o
campo ServerPath.

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
