# Shared helpers for build-portable-release.ps1 and build-release.ps1.
# Dot-source it: . (Join-Path $PSScriptRoot 'build-common.ps1')

$script:vsInstallPath = $null

# The CMake cache records the toolset chosen at configure time. Preferring VS 2022 avoids
# switching MSVC when another edition is installed, which would invalidate the incremental build.
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

# The Ninja generator does not set up the MSVC environment on its own: without INCLUDE and LIB,
# cl.exe cannot find corecrt.h and link.exe cannot find secur32.lib.
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
    param(
        [Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$ServerBuildPath,
        [Parameter(Mandatory)][string]$VcpkgRoot,
        [Parameter(Mandatory)][string]$CudaRoot
    )

    $commands = @()

    if (-not (Test-Path -LiteralPath (Join-Path $ServerBuildPath 'CMakeCache.txt'))) {
        $ninja = Get-NinjaPath
        $toolchain = Join-Path $VcpkgRoot 'scripts\buildsystems\vcpkg.cmake'
        if (-not (Test-Path -LiteralPath $toolchain)) {
            throw "O toolchain do vcpkg não foi encontrado em $toolchain. Use -VcpkgRoot para apontar a instalação."
        }

        $nvcc = Join-Path $CudaRoot 'bin\nvcc.exe'
        if (-not (Test-Path -LiteralPath $nvcc)) {
            throw "nvcc.exe não foi encontrado em $nvcc. Use -CudaRoot para apontar o toolkit CUDA."
        }

        # nvcc and the CCCL headers must come from the same toolkit; mixing installed versions
        # fails with "CUDA compiler and CUDA toolkit headers are incompatible".
        $commands += 'cmake -S "{0}" -B "{1}" -G "Ninja Multi-Config" -DCMAKE_MAKE_PROGRAM="{2}" -DCMAKE_CUDA_COMPILER="{3}" -DCMAKE_TOOLCHAIN_FILE="{4}" -DVCPKG_TARGET_TRIPLET=x64-windows' -f `
            $RepositoryRoot, $ServerBuildPath, $ninja.Replace('\', '/'), $nvcc.Replace('\', '/'), $toolchain.Replace('\', '/')
    }

    $commands += 'cmake --build "{0}" --config Release --parallel' -f $ServerBuildPath

    Invoke-InDeveloperShell -Commands $commands -FailureMessage 'A compilação do servidor nativo falhou'
}

function Resolve-ServerReleaseDirectory {
    param(
        [Parameter(Mandatory)][string]$ServerBuildPath
    )

    $releasePath = Join-Path $ServerBuildPath 'apps\Release'
    $executable = Join-Path $releasePath 'ninfer-serve.exe'
    if (-not (Test-Path -LiteralPath $executable)) {
        throw "ninfer-serve.exe não foi encontrado em $releasePath. Compile o servidor ou use -ServerBuildDirectory."
    }

    return $releasePath
}

# An NInferControl.exe running from the output folder blocks the cleanup with an
# "Access to the path is denied" that does not say what to do about it.
function Assert-NInferControlNotRunning {
    param(
        [Parameter(Mandatory)][string]$Path
    )

    $running = Get-Process -Name 'NInferControl' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($Path, [StringComparison]::OrdinalIgnoreCase) }
    if ($running) {
        throw "Feche o NInferControl.exe aberto a partir de $Path (PID $($running.Id -join ', ')) antes de gerar o pacote."
    }
}
