param(
  [string]$BackupRoot='D:\Development\PedidosYa\backups',
  [string]$SourceContainer='pedidosya-validation-postgres-1'
)
$ErrorActionPreference='Stop'
$workspace=Split-Path -Parent $PSScriptRoot
if ([IO.Path]::GetPathRoot([IO.Path]::GetFullPath($BackupRoot)) -ne 'D:\') { throw 'El respaldo debe permanecer en D.' }
if ($SourceContainer -ne 'pedidosya-validation-postgres-1') { throw 'Este ensayo solo admite el contenedor local de validación.' }
if (Get-NetTCPConnection -LocalPort 8080 -State Listen -ErrorAction SilentlyContinue) { throw 'Detén la API local antes del respaldo para mantener coherencia entre base y archivos.' }
$runId=[Guid]::NewGuid().ToString('N')
$runRoot=Join-Path $BackupRoot $runId
New-Item -ItemType Directory -Path $runRoot | Out-Null
$acl=Get-Acl -LiteralPath $runRoot
$acl.SetAccessRuleProtection($true,$false)
foreach($identity in @([Security.Principal.WindowsIdentity]::GetCurrent().User,[Security.Principal.SecurityIdentifier]::new('S-1-5-18'),[Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))) {
  $rule=[Security.AccessControl.FileSystemAccessRule]::new($identity,'FullControl','ContainerInherit,ObjectInherit','None','Allow')
  $acl.AddAccessRule($rule)
}
Set-Acl -LiteralPath $runRoot -AclObject $acl
$configSource=Join-Path $workspace '.env'
if(!(Test-Path -LiteralPath $configSource -PathType Leaf)){throw 'Falta configuración privada local.'}
Copy-Item -LiteralPath $configSource -Destination (Join-Path $runRoot 'secrets.env')
$validation=Join-Path $workspace '.artifacts/validation'
foreach($kind in @('keys','uploads')) {
  $source=Join-Path $validation $kind
  if (!(Test-Path -LiteralPath $source -PathType Container)) { throw "Falta el directorio local $kind." }
  Copy-Item -LiteralPath $source -Destination (Join-Path $runRoot $kind) -Recurse
}
$dumpName="delivery-restore-$runId.dump"
& docker --context desktop-linux exec $SourceContainer pg_dump -U delivery -d delivery -Fc -f "/tmp/$dumpName"
if($LASTEXITCODE -ne 0){throw 'Falló pg_dump.'}
& docker --context desktop-linux cp "${SourceContainer}:/tmp/$dumpName" (Join-Path $runRoot 'database.dump')
if($LASTEXITCODE -ne 0){throw 'Falló la copia del dump.'}
# Generated single-file path in /tmp, never a recursive or wildcard deletion.
& docker --context desktop-linux exec $SourceContainer rm "--" "/tmp/$dumpName"
$restoreContainer="pedidosya-restore-$runId"
try {
  & docker --context desktop-linux run -d --name $restoreContainer --label "pedidosya.restore-test=$runId" --network none -e POSTGRES_HOST_AUTH_METHOD=trust -e POSTGRES_DB=postgres postgis/postgis:17-3.5 | Out-Null
  if($LASTEXITCODE -ne 0){throw 'No se pudo crear el contenedor aislado.'}
  $ready=$false
  for($attempt=0;$attempt -lt 40;$attempt++) {
    $startup=& docker --context desktop-linux logs $restoreContainer 2>&1
    if("$startup" -match 'init process complete') {
      & docker --context desktop-linux exec $restoreContainer pg_isready -U postgres -d postgres *> $null
      if($LASTEXITCODE -eq 0){$ready=$true;break}
    }
    Start-Sleep -Milliseconds 500
  }
  if(!$ready){throw 'PostgreSQL de restauración no quedó listo.'}
  & docker --context desktop-linux exec $restoreContainer createdb -U postgres -T template0 delivery_restore
  if($LASTEXITCODE -ne 0){throw 'No se pudo crear base vacía de restauración.'}
  & docker --context desktop-linux cp (Join-Path $runRoot 'database.dump') "${restoreContainer}:/tmp/database.dump"
  if($LASTEXITCODE -ne 0){throw 'No se pudo copiar el dump al contenedor aislado.'}
  & docker --context desktop-linux exec $restoreContainer pg_restore -U postgres -d delivery_restore --no-owner --no-acl --exit-on-error /tmp/database.dump 2>&1 | Set-Content -Encoding utf8 (Join-Path $runRoot 'restore.log')
  if($LASTEXITCODE -ne 0){Get-Content (Join-Path $runRoot 'restore.log') -Tail 8;throw 'Falló pg_restore.'}
  $countSql=@"
SELECT jsonb_object_agg(name,rows) FROM (
 SELECT schemaname||'.'||tablename AS name,
 ((xpath('/row/n/text()',query_to_xml(format('SELECT count(*) AS n FROM %I.%I',schemaname,tablename),false,true,'')))[1]::text)::bigint AS rows
 FROM pg_tables WHERE schemaname NOT IN ('pg_catalog','information_schema','public')
) application_counts
"@
  $original=& docker --context desktop-linux exec $SourceContainer psql -U delivery -d delivery -At -c $countSql
  if($LASTEXITCODE -ne 0){throw 'Falló conteo de origen.'}
  $restored=& docker --context desktop-linux exec $restoreContainer psql -U postgres -d delivery_restore -At -c $countSql
  if($LASTEXITCODE -ne 0 -or "$original" -ne "$restored"){throw 'Los conteos de tablas no coinciden.'}
  $counts=$original | ConvertFrom-Json -AsHashtable
  $fileCounts=@{}
  foreach($kind in @('keys','uploads')) {
    $backup=Join-Path $runRoot $kind
    $restoredDirectory=Join-Path $runRoot "restored/$kind"
    New-Item -ItemType Directory -Path (Split-Path $restoredDirectory -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $backup -Destination $restoredDirectory -Recurse
    $files=@(Get-ChildItem -LiteralPath $backup -File -Recurse)
    if(!$files.Count){throw "El respaldo de $kind está vacío."}
    foreach($file in $files) {
      $relative=[IO.Path]::GetRelativePath($backup,$file.FullName)
      if((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path $restoredDirectory $relative)).Hash){throw 'Archivo restaurado distinto al respaldo.'}
      if((Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath (Join-Path (Join-Path $validation $kind) $relative)).Hash){throw 'Archivo respaldado distinto al origen.'}
    }
    $fileCounts[$kind]=$files.Count
  }
  Copy-Item -LiteralPath (Join-Path $runRoot 'secrets.env') -Destination (Join-Path $runRoot 'restored/secrets.env')
  $configHash=(Get-FileHash -LiteralPath $configSource).Hash
  if($configHash -ne (Get-FileHash -LiteralPath (Join-Path $runRoot 'secrets.env')).Hash -or $configHash -ne (Get-FileHash -LiteralPath (Join-Path $runRoot 'restored/secrets.env')).Hash){throw 'Configuración restaurada distinta.'}
  $fileCounts['configuration']=1
  $invariants=& docker --context desktop-linux exec $restoreContainer psql -U postgres -d delivery_restore -At -c "SELECT count(*) FROM (SELECT voucher_id FROM ledger.lines GROUP BY voucher_id HAVING sum(amount)<>0) unbalanced"
  if($LASTEXITCODE -ne 0 -or "$invariants" -ne '0'){throw 'La contabilidad restaurada no está balanceada.'}
  $summary=@{completedAt=[DateTimeOffset]::Now.ToString('o');tables=$counts.Count;tableCounts=$counts;files=$fileCounts;balanced=$true;isolated=$true}
  $summary | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 (Join-Path $runRoot 'verification.json')
  @{backupDirectory=$runRoot;tables=$counts.Count;keys=$fileCounts['keys'];documents=$fileCounts['uploads'];balanced=$true} | ConvertTo-Json
}
finally {
  $label=& docker --context desktop-linux inspect --format '{{index .Config.Labels "pedidosya.restore-test"}}' $restoreContainer 2>$null
  if($LASTEXITCODE -eq 0 -and "$label" -eq $runId) { & docker --context desktop-linux rm -f $restoreContainer | Out-Null }
}
