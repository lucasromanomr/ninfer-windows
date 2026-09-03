[CmdletBinding()]
param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\..\dist\ninfer-control-release')
)

$ErrorActionPreference = 'Stop'

$projectPath = Join-Path $PSScriptRoot 'NInferControl.csproj'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$outputPath = [System.IO.Path]::GetFullPath($OutputDirectory)
$zipPath = Join-Path (Split-Path $outputPath -Parent) 'NInferControl-Control-Release-x64.zip'

function Get-SignToolPath {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) {
        return $command.Source
    }

    $kitsRoot = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $candidate = Get-ChildItem -Path $kitsRoot -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if (-not $candidate) {
        throw 'signtool.exe não foi encontrado. Instale o Windows SDK Signing Tools.'
    }

    return $candidate.FullName
}

function New-ReleaseSigningCertificate {
    $now = Get-Date
    $existing = Get-ChildItem Cert:\CurrentUser\My | Where-Object {
        $_.FriendlyName -eq 'NInfer Control Release Test Signing' -and $_.Subject -eq 'CN=NInfer' -and $_.Issuer -eq 'CN=NInfer' -and $_.NotAfter -gt $now.AddDays(30)
    } | Sort-Object NotAfter -Descending | Select-Object -First 1
    if ($existing) {
        return $existing
    }

    return New-SelfSignedCertificate -Type Custom -Subject 'CN=NInfer' -FriendlyName 'NInfer Control Release Test Signing' -KeyUsage DigitalSignature -KeyExportPolicy Exportable -KeySpec Signature -HashAlgorithm SHA256 -NotAfter $now.AddYears(2) -CertStoreLocation Cert:\CurrentUser\My -TextExtension @('2.5.29.19={critical}{text}ca=false', '2.5.29.37={text}1.3.6.1.5.5.7.3.3')
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK não encontrado.'
}

$signTool = Get-SignToolPath
$signingCertificate = New-ReleaseSigningCertificate

if (Test-Path -LiteralPath $outputPath) {
    Remove-Item -LiteralPath $outputPath -Recurse -Force
}
New-Item -ItemType Directory -Path $outputPath | Out-Null

& dotnet publish $projectPath -c Release -p:Platform=x64 -p:GenerateAppxPackageOnBuild=true "-p:AppxPackageDir=$outputPath\"
if ($LASTEXITCODE -ne 0) {
    throw "A publicação Release falhou com código $LASTEXITCODE."
}

$packageDirectory = Get-ChildItem -LiteralPath $outputPath -Directory |
    Where-Object { Test-Path (Join-Path $_.FullName 'Install.ps1') } |
    Select-Object -First 1
if (-not $packageDirectory) {
    throw 'A pasta do instalador MSIX não foi gerada.'
}

$msix = Get-ChildItem -LiteralPath $packageDirectory.FullName -Filter *.msix | Select-Object -First 1
if (-not $msix) {
    throw 'O arquivo MSIX não foi gerado.'
}

& $signTool sign /fd SHA256 /sha1 $signingCertificate.Thumbprint /s My /v $msix.FullName
if ($LASTEXITCODE -ne 0) {
    throw "A assinatura do MSIX falhou com código $LASTEXITCODE."
}

$leafCertificatePath = Join-Path $packageDirectory.FullName 'NInferControl_1.0.0.0_x64.cer'
Export-Certificate -Cert $signingCertificate -FilePath $leafCertificatePath -Force | Out-Null

$generatedInstallerPath = Join-Path $packageDirectory.FullName 'Install.ps1'
if (Test-Path -LiteralPath $generatedInstallerPath) {
    Remove-Item -LiteralPath $generatedInstallerPath -Force
}

$installerPath = Join-Path $packageDirectory.FullName 'Install-Release.ps1'
$installer = @'
[CmdletBinding()]
param(
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    if ($Force) { $arguments += ' -Force' }
    $elevatedProcess = Start-Process -FilePath powershell.exe -Verb RunAs -ArgumentList $arguments -Wait -PassThru
    if ($elevatedProcess.ExitCode -ne 0) {
        throw "A instalação elevada falhou com código $($elevatedProcess.ExitCode)."
    }
    return
}

$signingCertificate = Join-Path $PSScriptRoot 'NInferControl_1.0.0.0_x64.cer'
$package = Get-ChildItem -LiteralPath $PSScriptRoot -Filter *.msix | Select-Object -First 1
$dependencyDirectory = Join-Path $PSScriptRoot 'Dependencies\x64'

if (-not (Test-Path -LiteralPath $signingCertificate)) {
    throw 'O certificado de assinatura não foi encontrado.'
}

if (-not $package) {
    throw 'O arquivo MSIX não foi encontrado.'
}

$packageSigner = Get-AuthenticodeSignature -FilePath $package.FullName
if ($packageSigner.SignerCertificate -eq $null) {
    throw 'O pacote MSIX não possui uma assinatura reconhecível.'
}

$releaseCertificate = Get-PfxCertificate -FilePath $signingCertificate
if ($packageSigner.SignerCertificate.Thumbprint -ne $releaseCertificate.Thumbprint) {
    throw 'O certificado fornecido não corresponde ao certificado que assinou o pacote MSIX.'
}

Import-Certificate -FilePath $signingCertificate -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
$trustedCertificate = Get-ChildItem -LiteralPath 'Cert:\LocalMachine\TrustedPeople' |
    Where-Object { $_.Thumbprint -eq $releaseCertificate.Thumbprint } |
    Select-Object -First 1
if (-not $trustedCertificate) {
    throw 'Não foi possível registrar o certificado de assinatura em LocalMachine\\TrustedPeople.'
}

$packageSigner = Get-AuthenticodeSignature -FilePath $package.FullName
if ($packageSigner.Status -ne [System.Management.Automation.SignatureStatus]::Valid) {
    throw "A assinatura do MSIX não ficou confiável após a importação: $($packageSigner.Status) - $($packageSigner.StatusMessage)"
}

$dependencies = if (Test-Path -LiteralPath $dependencyDirectory) {
    Get-ChildItem -LiteralPath $dependencyDirectory -File |
        Where-Object { $_.Extension -in '.appx', '.msix' } |
        Select-Object -ExpandProperty FullName
}

if ($dependencies) {
    Add-AppxPackage -Path $package.FullName -DependencyPath $dependencies -ForceApplicationShutdown
}
else {
    Add-AppxPackage -Path $package.FullName -ForceApplicationShutdown
}
'@
Set-Content -LiteralPath $installerPath -Value $installer -Encoding utf8

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
Compress-Archive -Path (Join-Path $packageDirectory.FullName '*') -DestinationPath $zipPath -CompressionLevel Optimal

Write-Host "MSIX: $($msix.FullName)"
Write-Host "Instalador: $installerPath"
Write-Host "ZIP: $zipPath"
