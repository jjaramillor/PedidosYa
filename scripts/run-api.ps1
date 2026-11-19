param([string]$BootstrapAdminEmail,[switch]$Validation)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $projectRoot '.env'
if (-not (Test-Path -LiteralPath $envFile)) { throw 'Copia .env.example a .env y configura las claves locales.' }
$values = @{}
foreach ($line in Get-Content -LiteralPath $envFile) {
    if ($line -match '^([A-Z_]+)=(.*)$') { $values[$matches[1]] = $matches[2] }
}
foreach ($required in @('POSTGRES_PASSWORD', 'JWT_KEY', 'OTP_KEY')) {
    if (-not $values[$required]) { throw "Falta $required en .env." }
}
$connection = [System.Data.Common.DbConnectionStringBuilder]::new()
$connection['Host'] = 'localhost'
$connection['Database'] = 'delivery'
$connection['Username'] = 'delivery'
$connection['Password'] = $values['POSTGRES_PASSWORD']
$settings = @{
    'ASPNETCORE_ENVIRONMENT' = 'Development'
    'ConnectionStrings__Postgres' = $connection.ConnectionString
    'ConnectionStrings__Redis' = 'localhost:6379'
    'Identity__JwtKey' = $values['JWT_KEY']
    'Identity__OtpKey' = $values['OTP_KEY']
    'Identity__SmtpTls' = 'false'
    'OTEL_EXPORTER_OTLP_ENDPOINT' = 'http://localhost:4317'
}
$previous = @{}
if ($Validation) {
    $validationRoot = Join-Path $projectRoot '.artifacts/validation'
    $settings['Payments__Provider'] = 'sandbox'
    $settings['Payments__AccessToken'] = ''
    $settings['Payments__PublicKey'] = ''
    $settings['Payments__WebhookSecret'] = ''
    $settings['Identity__SmtpHost'] = '127.0.0.1'
    $settings['Identity__SmtpPort'] = '1025'
    $settings['Identity__SmtpUser'] = ''
    $settings['Identity__SmtpPassword'] = ''
    $settings['Identity__Sender'] = 'no-reply@delivery.test'
    $settings['OTEL_EXPORTER_OTLP_ENDPOINT'] = ''
    $settings['DataProtection__KeyPath'] = Join-Path $validationRoot 'keys'
    $settings['Documents__Root'] = Join-Path $validationRoot 'uploads'
    $settings['BootstrapAdminEmail'] = ''
}
try {
    foreach ($name in $settings.Keys) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
        [Environment]::SetEnvironmentVariable($name, $settings[$name], 'Process')
    }
    if ($BootstrapAdminEmail) {
        dotnet run --project (Join-Path $projectRoot 'src/Delivery.Api') --no-launch-profile -- "--BootstrapAdminEmail=$BootstrapAdminEmail"
    }
    else {
        dotnet run --project (Join-Path $projectRoot 'src/Delivery.Api') --no-launch-profile --urls http://localhost:8080
    }
    if ($LASTEXITCODE -ne 0) { throw "La API terminó con código $LASTEXITCODE." }
}
finally {
    foreach ($name in $settings.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
}
