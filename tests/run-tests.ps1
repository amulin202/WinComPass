param([switch]$UI, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$project = Split-Path $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $project 'dist\tests' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$output = Join-Path $OutputDirectory 'CoverageTests.exe'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
$wpf = Join-Path (Split-Path $compiler) 'WPF'
$sources = 'CompassVector.cs', 'CoverageMath.cs', 'CoverageCoordinates.cs', 'CoveragePreview.cs',
    'CoverageWindow.cs', 'OvitClient.cs', 'tests\CoverageTests.cs' | ForEach-Object { Join-Path $project $_ }
$notices = Join-Path $project 'ThirdPartyNotices.txt'
& $compiler /nologo /target:exe /main:CoverageTests /platform:anycpu /optimize+ `
    /out:$output /reference:System.dll /reference:System.Core.dll /reference:System.Xml.dll `
    /reference:System.Web.Extensions.dll /reference:System.Xaml.dll `
    /reference:"$wpf\WindowsBase.dll" /reference:"$wpf\PresentationCore.dll" `
    /reference:"$wpf\PresentationFramework.dll" /resource:"$notices,Compass.ThirdPartyNotices" $sources
if ($LASTEXITCODE -ne 0) { throw "测试编译失败，退出码 $LASTEXITCODE" }
$testArgs = @()
if ($UI) { $testArgs += '--ui'; $testArgs += $OutputDirectory }
& $output $testArgs
if ($LASTEXITCODE -ne 0) { throw "测试失败，退出码 $LASTEXITCODE" }
