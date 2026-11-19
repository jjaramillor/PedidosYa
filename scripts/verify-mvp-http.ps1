param([string]$ApiBase='http://localhost:8080',[string]$MailpitBase='http://localhost:8025')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
foreach($address in @($ApiBase,$MailpitBase)){
    if(([uri]$address).Host -notin @('localhost','127.0.0.1','::1')){throw 'La validación solo admite servicios locales.'}
}
$runTag=[guid]::NewGuid().ToString('N').Substring(0,8)
$branchLongitude=-75+(Get-Random -Minimum 1 -Maximum 4000000)/1000000.0
$addressLongitude=$branchLongitude-0.01
$polygon=(@{type='Polygon';coordinates=@(,@(@(($branchLongitude-0.03),-12.1),@(($branchLongitude+0.03),-12.1),@(($branchLongitude+0.03),-12),@(($branchLongitude-0.03),-12),@(($branchLongitude-0.03),-12.1))) }|ConvertTo-Json -Depth 10 -Compress)
function Api([string]$Path,[string]$Method='GET',$Body=$null,[string]$Token=''){
    $parameters=@{Uri="$ApiBase/api/v1$Path";Method=$Method}
    if($Token){$parameters.Headers=@{Authorization="Bearer $Token"}}
    if($null -ne $Body){$parameters.Body=ConvertTo-Json -InputObject $Body -Depth 20;$parameters.ContentType='application/json'}
    try { Invoke-RestMethod @parameters } catch { throw "HTTP $Method $Path : $($_.ErrorDetails.Message)" }
}
function Register([string]$Role){
    $email="$Role-$runTag@mvp.test"
    $policies=Api '/identity/policies'
    $challenge=Api '/identity/otp/request' 'POST' @{email=$email;displayName="Prueba $Role";acceptTerms=$true;acceptPrivacy=$true;purpose='register';deviceName='Validación HTTP local';termsVersion=$policies.termsVersion;privacyVersion=$policies.privacyVersion}
    $code=$null
    for($attempt=0;$attempt -lt 40;$attempt++){
        $messages=Invoke-RestMethod "$MailpitBase/api/v1/messages"
        foreach($message in $messages.messages){
            if(@($message.To|ForEach-Object{$_.Address}) -contains $email){
                $content=Invoke-RestMethod "$MailpitBase/api/v1/message/$($message.ID)"
                if($content.Text -match '(?<!\d)\d{6}(?!\d)'){$code=$matches[0];break}
            }
        }
        if($code){break};Start-Sleep -Milliseconds 250
    }
    if(-not $code){throw "No llegó el OTP local para $Role."}
    Api '/identity/otp/verify' 'POST' @{challengeId=$challenge.challengeId;code=$code}
}
function Document([string]$Token){
    $client=[System.Net.Http.HttpClient]::new()
    $client.DefaultRequestHeaders.Authorization=[System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer',$Token)
    $form=[System.Net.Http.MultipartFormDataContent]::new()
    $content=[System.Net.Http.ByteArrayContent]::new([Text.Encoding]::ASCII.GetBytes('%PDF-1.4 Documento ficticio de validacion'))
    $content.Headers.ContentType=[System.Net.Http.Headers.MediaTypeHeaderValue]::new('application/pdf')
    $form.Add($content,'file','validacion-ficticia.pdf')
    try{$response=$client.PostAsync("$ApiBase/api/v1/documents",$form).GetAwaiter().GetResult();$response.EnsureSuccessStatusCode()|Out-Null;($response.Content.ReadAsStringAsync().GetAwaiter().GetResult()|ConvertFrom-Json).id}
    finally{$form.Dispose();$client.Dispose()}
}
function WaitOrder([string]$Id,[string]$Expected,[string]$Token){
    for($attempt=0;$attempt -lt 60;$attempt++){
        $current=Api "/orders/$Id" -Token $Token
        if($current.state -eq $Expected){return $current}
        if($current.state -eq 'cancelled'){throw "El pedido fue cancelado mientras se esperaba $Expected."}
        Start-Sleep -Milliseconds 250
    }
    throw "El pedido no llegó al estado $Expected."
}
Write-Host 'Creando cuatro cuentas ficticias y verificando correo OTP local...'
$admin=Register 'admin';$customer=Register 'customer';$merchant=Register 'merchant';$courier=Register 'courier'
$settings=Api '/payments/settings' -Token $admin.accessToken
if($settings.provider -ne 'sandbox'){throw 'La validación requiere Payments:Provider=sandbox.'}
& (Join-Path $PSScriptRoot 'run-api.ps1') -Validation -BootstrapAdminEmail $admin.user.email
$at=$admin.accessToken;$ct=$customer.accessToken;$mt=$merchant.accessToken;$rt=$courier.accessToken
Write-Host 'Configurando zona, comercio, carta y repartidor de prueba...'
$zone=Api '/admin/zones' 'POST' @{name="Zona QA $runTag";polygonGeoJson=$polygon;active=$true;deliveryBase=500;perKilometer=100;serviceFee=100;commissionBasisPoints=1500;courierBase=400;acceptanceSeconds=120;cashEnabled=$true;reason='Validación local con datos ficticios'} $at
Api '/admin/parameters/cash.enabled' 'PUT' @{value=$true;reason='Validación local de efectivo'} $at|Out-Null
$business=Api '/merchants' 'POST' @{name="Restaurante QA $runTag";taxId="FICTICIO-$runTag";bankAccount='CUENTA FICTICIA';documentIds=@((Document $mt))} $mt
Api "/merchants/$($business.id)/decision" 'PUT' @{status='approved';reason='Documentación ficticia de pruebas'} $at|Out-Null
$branch=Api "/merchants/$($business.id)/branches" 'POST' @{name="Sucursal QA $runTag";location=@{latitude=-12.04;longitude=$branchLongitude};minimumOrder=0;preparationMinutes=15;active=$true;hours=@(0..6|ForEach-Object{@{day=$_;fromMinute=0;toMinute=1440}});pausedUntil=$null} $mt
$product=Api "/merchant/branches/$($branch.id)/products" 'POST' @{name='Menú de validación';category='Menús';description='Producto ficticio para pruebas locales';imageUrl=$null;allergens=@();price=2500;stock=10;available=$true;modifiers=@()} $mt
$address=Api '/addresses' 'POST' @{label='Dirección ficticia';street='Dirección de prueba';reference='Sin entrega real';location=@{latitude=-12.05;longitude=$addressLongitude}} $ct
Api '/couriers/apply' 'POST' @{vehicle='bicycle';documentIds=@((Document $rt))} $rt|Out-Null
Api "/admin/couriers/$($courier.user.id)/decision" 'PUT' @{status='approved';reason='Repartidor ficticio de validación'} $at|Out-Null
$orders=@()
foreach($method in @('card','cash')){
    Write-Host "Validando recorrido HTTP completo con $method..."
    Api '/couriers/availability' 'PUT' @{available=$true;zoneId=$zone.id;location=@{latitude=-12.04;longitude=$branchLongitude}} $rt|Out-Null
    $quote=Api '/quotes' 'POST' @{addressId=$address.id;branchId=$branch.id;lines=@(@{productId=$product.id;quantity=1;modifierIds=@()});coupon=$null;tip=200} $ct
    $token=if($method -eq 'card'){'{"token":"sandbox-approved","paymentMethodId":"visa","issuerId":null}'}else{$null}
    $placed=Api '/orders' 'POST' @{operationId=[guid]::NewGuid().ToString();quoteId=$quote.id;method=$method;cardToken=$token} $ct
    $id=$placed.id;WaitOrder $id 'confirmed' $ct|Out-Null
    Api "/orders/$id/actions" 'POST' @{operationId=[guid]::NewGuid().ToString();state='accepted';reason='Aceptación de prueba'} $mt|Out-Null
    $offer=$null
    for($attempt=0;$attempt -lt 40;$attempt++){$offer=(Api '/couriers/offers' -Token $rt)|ForEach-Object{$_}|Where-Object orderId -eq $id|Select-Object -First 1;if($offer){break};Start-Sleep -Milliseconds 250}
    if(-not $offer){throw 'No se generó una oferta para el repartidor.'}
    Api "/couriers/offers/$($offer.id)/decision" 'POST' @{accept=$true} $rt|Out-Null
    foreach($state in @('preparing','ready')){Api "/orders/$id/actions" 'POST' @{operationId=[guid]::NewGuid().ToString();state=$state;reason='Operación comercial de prueba'} $mt|Out-Null}
    $delivery=$null
    for($attempt=0;$attempt -lt 40;$attempt++){
        try{$delivery=Api "/deliveries/$id" -Token $mt;break}catch{Start-Sleep -Milliseconds 250}
    }
    if(-not $delivery){throw 'No se creó la entrega.'}
    Api "/deliveries/$id/actions" 'POST' @{operationId=[guid]::NewGuid().ToString();state='arrived';reason='Llegada de prueba'} $rt|Out-Null
    Api "/deliveries/$id/actions" 'POST' @{operationId=[guid]::NewGuid().ToString();state='picked_up';pin=$delivery.pickupPin;reason='Retiro de prueba'} $rt|Out-Null
    WaitOrder $id 'picked_up' $ct|Out-Null
    Api "/deliveries/$id/actions" 'POST' @{operationId=[guid]::NewGuid().ToString();state='in_transit';reason='Ruta de prueba'} $rt|Out-Null
    WaitOrder $id 'in_transit' $ct|Out-Null
    Api "/deliveries/$id/location" 'POST' @{latitude=-12.045;longitude=($branchLongitude-0.005)} $rt|Out-Null
    $tracking=Api "/deliveries/$id" -Token $ct
    if(-not $tracking.lastLocation){throw 'La ubicación de prueba no se muestra al cliente.'}
    Api "/deliveries/$id/actions" 'POST' @{operationId=[guid]::NewGuid().ToString();state='delivered';pin=$tracking.deliveryPin;cashCollected=($method -eq 'cash');reason='Entrega ficticia verificada'} $rt|Out-Null
    WaitOrder $id 'closed' $ct|Out-Null
    $payment=Api "/orders/$id/payment" -Token $ct
    if($payment.captured -ne $quote.total){throw 'El importe capturado no coincide con la cotización.'}
    Api "/orders/$id/rating" 'POST' @{merchantStars=5;courierStars=5;comment='Validación local sin compra real'} $ct|Out-Null
    $orders+=@{id=$id;method=$method;state='closed';total=$quote.total}
}
$statement=Api "/statements/merchant/$($business.id)" -Token $mt
$settlement=Api "/admin/settlements/merchant/$($business.id)" 'POST' @{operationId=[guid]::NewGuid().ToString();reason='Cierre ficticio para validación'} $at
Api "/admin/settlements/$($settlement.id)/paid" 'PUT' @{externalReference="SIMULADO-$runTag";reason='Registro ficticio: no se hizo transferencia'} $at|Out-Null
$summary=@{run=$runTag;provider='sandbox';orders=$orders;merchantId=$business.id;branchId=$branch.id;productId=$product.id;customerEmail=$customer.user.email;merchantEmail=$merchant.user.email;courierEmail=$courier.user.email;adminEmail=$admin.user.email;settlementId=$settlement.id;merchantPendingBeforeSettlement=$statement.pending}
$artifactRoot=Join-Path $root '.artifacts/validation';New-Item -ItemType Directory -Path $artifactRoot -Force|Out-Null
$summary|ConvertTo-Json -Depth 10|Set-Content (Join-Path $artifactRoot 'ultimo-recorrido-http.json') -Encoding utf8
Write-Host 'Recorridos HTTP completados; dos pedidos cerrados y liquidación ficticia registrada. No se realizaron cobros ni transferencias reales.'







