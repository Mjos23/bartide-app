param(
    [switch]$Probe,
    [string]$ReportPath
)

$ErrorActionPreference = 'Stop'
$sourceRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$runtimePath = Join-Path $sourceRoot '.tools/dotnet-10.0.401/dotnet.exe'
$assemblyPath = Join-Path $sourceRoot 'TideCasa.Stripe.Checks/bin/Debug/net10.0/TideCasa.Stripe.Checks.dll'
if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) {
    throw 'Build TideCasa.Stripe.Checks before running the sandbox readiness check.'
}
if ([string]::IsNullOrWhiteSpace($ReportPath)) {
    $ReportPath = Join-Path $sourceRoot 'output/stripe-api/sandbox-preflight.json'
}
$checkArguments = @($assemblyPath, '--sandbox-preflight', $sourceRoot, $ReportPath)
if ($Probe) { $checkArguments += '--probe' }
& $runtimePath @checkArguments
exit $LASTEXITCODE
