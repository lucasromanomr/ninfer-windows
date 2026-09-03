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

$projectPath = Join-Path $PSScriptRoot 'NInferControl.csproj'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
$serverBuildPath = [System.IO.Path]::GetFullPath($ServerBuildDirectory)
$zipPath = Join-Path (Split-Path $outputPath -Parent) 'NInferControl-Portable-x64.zip'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK não encontrado.'
}

$script:vsInstallPath = $null

# O cache do CMake grava o toolset usado no configure. Preferir o VS 2022 evita trocar de
# MSVC quando há outra edição instalada, o que invalidaria o build incremental.
function Get-VsInstallPath {
    if ($script:vsInstallPath) {
        return $script:vsInstallPath
    }

    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path -LiteralPath $vswhere)) {
        throw 'vswhere.exe não foi encontrado. Instale o Visual Studio 2022 com a carga de trabalho C++.'
    }

    $requires = 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64'
    $installation = & $vswhere -version '[17.0,18.0)' -products * -requires $requires -property installationPath |
        Select-Object -First 1
    if (-not $installation) {
        $installation = & $vswhere -latest -products * -requires $requires -property installationPath |
            Select-Object -First 1
    }
    if (-not $installation) {
        throw 'Nenhuma instalação do Visual Studio com as ferramentas C++ x64 foi encontrada.'
    }

    $script:vsInstallPath = $installation
    return $installation
}

function Get-VsDevCmdPath {
    $installation = Get-VsInstallPath
    $vsDevCmd = Join-Path $installation 'Common7\Tools\VsDevCmd.bat'
    if (-not (Test-Path -LiteralPath $vsDevCmd)) {
        throw "VsDevCmd.bat não foi encontrado em $installation."
    }

    return $vsDevCmd
}

function Get-NinjaPath {
    $command = Get-Command ninja.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) {
        return $command.Source
    }

    $bundled = Join-Path (Get-VsInstallPath) 'Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe'
    if (Test-Path -LiteralPath $bundled) {
        return $bundled
    }

    throw 'ninja.exe não foi encontrado no PATH nem na instalação do Visual Studio.'
}

# O gerador Ninja não monta o ambiente MSVC sozinho: sem INCLUDE/LIB o cl.exe não acha
# corecrt.h e o link.exe não acha secur32.lib. Por isso cmake roda dentro do VsDevCmd.
function Invoke-InDeveloperShell {
    param(
        [Parameter(Mandatory)][string[]]$Commands,
        [Parameter(Mandatory)][string]$FailureMessage
    )

    $vsDevCmd = Get-VsDevCmdPath
    $scriptPath = [System.IO.Path]::Combine([System.IO.Path]::GetTempPath(), "ninfer-build-$([guid]::NewGuid().ToString('N')).cmd")

    $lines = @(
        '@echo off',
        "call `"$vsDevCmd`" -arch=x64 -host_arch=x64 >nul",
        'if errorlevel 1 exit /b 1'
    ) + ($Commands | ForEach-Object { @($_, 'if errorlevel 1 exit /b 1') })

    Set-Content -LiteralPath $scriptPath -Value $lines -Encoding ascii
    try {
        & cmd.exe /c "`"$scriptPath`""
        if ($LASTEXITCODE -ne 0) {
            throw "$FailureMessage (código $LASTEXITCODE)."
        }
    }
    finally {
        Remove-Item -LiteralPath $scriptPath -Force -ErrorAction SilentlyContinue
    }
}

function Build-NativeServer {
    $ninja = Get-NinjaPath
    $commands = @()

    if (-not (Test-Path -LiteralPath (Join-Path $serverBuildPath 'CMakeCache.txt'))) {
        $toolchain = Join-Path $VcpkgRoot 'scripts\buildsystems\vcpkg.cmake'
        if (-not (Test-Path -LiteralPath $toolchain)) {
            throw "O toolchain do vcpkg não foi encontrado em $toolchain. Use -VcpkgRoot para apontar a instalação."
        }

        $nvcc = Join-Path $CudaRoot 'bin\nvcc.exe'
        if (-not (Test-Path -LiteralPath $nvcc)) {
            throw "nvcc.exe não foi encontrado em $nvcc. Use -CudaRoot para apontar o toolkit CUDA."
        }

        # O nvcc e os headers CCCL precisam vir do mesmo toolkit; misturar versões
        # instaladas quebra o build com "CUDA compiler and CUDA toolkit headers are incompatible".
        $commands += 'cmake -S "{0}" -B "{1}" -G "Ninja Multi-Config" -DCMAKE_MAKE_PROGRAM="{2}" -DCMAKE_CUDA_COMPILER="{3}" -DCMAKE_TOOLCHAIN_FILE="{4}" -DVCPKG_TARGET_TRIPLET=x64-windows' -f `
            $repositoryRoot, $serverBuildPath, $ninja.Replace('\', '/'), $nvcc.Replace('\', '/'), $toolchain.Replace('\', '/')
    }

    $commands += 'cmake --build "{0}" --config Release --parallel' -f $serverBuildPath

    Invoke-InDeveloperShell -Commands $commands -FailureMessage 'A compilação do servidor nativo falhou'
}

if ($SkipServerBuild) {
    Write-Host 'Compilação do servidor nativo ignorada (-SkipServerBuild).'
}
else {
    Write-Host "Compilando o servidor nativo em $serverBuildPath..."
    Build-NativeServer
}

$serverReleasePath = Join-Path $serverBuildPath 'apps\Release'
$serverExecutable = Join-Path $serverReleasePath 'ninfer-serve.exe'
if (-not (Test-Path -LiteralPath $serverExecutable)) {
    throw "ninfer-serve.exe não foi encontrado em $serverReleasePath. Compile o servidor ou use -ServerBuildDirectory."
}

# Um NInferControl.exe aberto a partir da pasta de saída trava a limpeza com um
# "Access to the path is denied" que não diz o que fazer.
$running = Get-Process -Name 'NInferControl' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($outputPath, [StringComparison]::OrdinalIgnoreCase) }
if ($running) {
    throw "Feche o NInferControl.exe aberto a partir de $outputPath (PID $($running.Id -join ', ')) antes de gerar o pacote."
}

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

# ninfer-serve.exe fica ao lado do NInferControl.exe: é o primeiro candidato de
# FindServerExecutable, então o app resolve o servidor sem configuração manual.
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
