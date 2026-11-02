param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'

$sources = 'CompassVector.cs', 'CoverageMath.cs', 'CoverageCoordinates.cs',
    'CoveragePreview.cs', 'CoverageWindow.cs', 'OvitClient.cs' |
    ForEach-Object { Join-Path $PSScriptRoot $_ }
$notices = Join-Path $PSScriptRoot 'ThirdPartyNotices.txt'
$icon = Join-Path $PSScriptRoot 'icon.ico'
$manifest = Join-Path $PSScriptRoot 'app.manifest'
if (-not $OutputDirectory) { $OutputDirectory = $PSScriptRoot }
$output = Join-Path $outputDirectory 'Compass x64_x86.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
$wpf = Join-Path (Split-Path $compiler) 'WPF'

if (-not (Test-Path -LiteralPath $compiler)) {
    throw '未找到 .NET Framework C# 编译器。'
}

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /debug- `
    /out:$output `
    /win32icon:$icon `
    /win32manifest:$manifest `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Xml.dll `
    /reference:System.Web.Extensions.dll `
    /reference:System.Xaml.dll `
    /reference:"$wpf\WindowsBase.dll" `
    /reference:"$wpf\PresentationCore.dll" `
    /reference:"$wpf\PresentationFramework.dll" `
    /resource:"$notices,Compass.ThirdPartyNotices" `
    $sources

if ($LASTEXITCODE -ne 0) {
    throw "编译失败，退出码 $LASTEXITCODE"
}

Remove-Item -LiteralPath ([IO.Path]::ChangeExtension($output, '.pdb')), `
    ($output + '.config') -Force -ErrorAction SilentlyContinue

Get-FileHash -Algorithm SHA256 -LiteralPath $output
