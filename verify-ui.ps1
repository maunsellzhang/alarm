param(
    [switch]$SkipBuild
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
if (-not $SkipBuild) {
    & (Join-Path $projectRoot 'build.ps1')
}

$frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
if (-not (Test-Path -LiteralPath (Join-Path $frameworkRoot 'csc.exe'))) {
    $frameworkRoot = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319'
}
$compilerPath = Join-Path $frameworkRoot 'csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) {
    throw 'The .NET Framework compiler is missing. Enable .NET Framework 4.8 and retry.'
}

$outputDirectory = Join-Path $projectRoot '.artifacts\ui-tests'
[void](New-Item -ItemType Directory -Path $outputDirectory -Force)
$testPath = Join-Path $outputDirectory 'UiTests.exe'
$sources = @((Join-Path $projectRoot 'Program.cs'))
$sources += @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'Core') -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName })
$sources += @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'UI') -Filter '*.cs' | Sort-Object Name | ForEach-Object { $_.FullName })
$sources += (Join-Path $projectRoot 'Tests\UiTests.cs')
$compilerArguments = @(
    '/nologo', '/langversion:5', '/target:exe', '/optimize+', '/main:QingReminder.Tests.UiTests',
    "/out:$testPath",
    "/win32manifest:$(Join-Path $projectRoot 'app.manifest')",
    "/resource:$(Join-Path $projectRoot 'Assets\Reminder.ico'),QingReminder.Icon",
    "/resource:$(Join-Path $projectRoot 'UI\Theme.xaml'),QingReminder.Theme.xaml",
    "/resource:$(Join-Path $projectRoot 'UI\MainWindow.xaml'),QingReminder.MainWindow.xaml",
    '/reference:System.dll', '/reference:System.Core.dll',
    '/reference:System.Drawing.dll', '/reference:System.Windows.Forms.dll',
    '/reference:System.Runtime.Serialization.dll', '/reference:System.Xaml.dll',
    "/reference:$(Join-Path $frameworkRoot 'WPF\WindowsBase.dll')",
    "/reference:$(Join-Path $frameworkRoot 'WPF\PresentationCore.dll')",
    "/reference:$(Join-Path $frameworkRoot 'WPF\PresentationFramework.dll')"
)
& $compilerPath $compilerArguments $sources
if ($LASTEXITCODE -ne 0) {
    throw "UI test compilation failed with exit code $LASTEXITCODE."
}
& $testPath $outputDirectory
if ($LASTEXITCODE -ne 0) {
    throw "UI tests failed with exit code $LASTEXITCODE."
}
