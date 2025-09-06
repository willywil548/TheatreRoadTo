param (
    [string]$JsonFilePath,
    [string]$EnvVarName
)

$envValue = [Environment]::GetEnvironmentVariable($EnvVarName)
if (-not $envValue) {
    Write-Host "Environment variable '$EnvVarName' not set. Skipping update."
    exit 0
}

if (-not (Test-Path $JsonFilePath)) {
    Write-Host "File '$JsonFilePath' does not exist."
    exit 0
}

$json = Get-Content $JsonFilePath | ConvertFrom-Json
$json.associatedApplications[0].applicationId = $envValue
$json | ConvertTo-Json -Depth 10 | Set-Content $JsonFilePath
Write-Host "Updated '$JsonFilePath' with applicationId='$envValue'."