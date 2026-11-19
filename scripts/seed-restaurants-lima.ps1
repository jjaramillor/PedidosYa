param([string]$ApiBase='http://localhost:8080',[string]$MailpitBase='http://localhost:8025')
$ErrorActionPreference='Stop'
foreach($address in @($ApiBase,$MailpitBase)){if(([uri]$address).Host -notin @('localhost','127.0.0.1','::1')){throw 'Solo se admite carga local de demostración.'}}
$root=Split-Path -Parent $PSScriptRoot
function Api([string]$Path,[string]$Method='GET',$Body=$null,[string]$Token=''){
    $parameters=@{Uri="$ApiBase/api/v1$Path";Method=$Method}
    if($Token){$parameters.Headers=@{Authorization="Bearer $Token"}}
    if($null -ne $Body){$parameters.Body=ConvertTo-Json -InputObject $Body -Depth 20;$parameters.ContentType='application/json'}
    try { Invoke-RestMethod @parameters } catch { throw "HTTP $Method $Path : $($_.ErrorDetails.Message)" }
}
function Session([string]$email){
    $challenge=Api '/identity/login' 'POST' @{email=$email;deviceName='Carga local de restaurantes demo'}
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
    if(-not $code){throw "No llegó el OTP local para la cuenta de demostración."}
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

$previous=Get-Content (Join-Path $root '.artifacts/validation/ultimo-recorrido-http.json') -Raw|ConvertFrom-Json
$admin=Session $previous.adminEmail
$merchant=Session $previous.merchantEmail
$at=$admin.accessToken;$mt=$merchant.accessToken
if((Api '/payments/settings' -Token $at).provider -ne 'sandbox'){throw 'Requiere proveedor sandbox.'}
$statePath=Join-Path $root '.artifacts/validation/restaurantes-lima-demo.json'
$state=if(Test-Path $statePath){Get-Content $statePath -Raw|ConvertFrom-Json}else{@{restaurants=@()}}
$businesses=Api '/merchants' -Token $mt
$zones=Api '/admin/zones' -Token $at
$zoneName='Lima central - DEMO (cobertura aproximada)'
if(-not ($zones|ForEach-Object{$_}|Where-Object {$_.configuration.name -eq $zoneName})){
 Api '/admin/zones' 'POST' @{name=$zoneName;polygonGeoJson='{"type":"Polygon","coordinates":[[[-77.09,-12.16],[-76.98,-12.16],[-76.98,-12.04],[-77.09,-12.04],[-77.09,-12.16]]]}';active=$true;deliveryBase=500;perKilometer=100;serviceFee=100;commissionBasisPoints=1500;courierBase=400;acceptanceSeconds=300;cashEnabled=$true;reason='Datos ficticios para probar restaurantes de Lima; no son límites distritales oficiales'} $at|Out-Null
}
$definitions=@(
 @{district='Lince';lat=-12.084;lon=-77.036;name='Fogón de Lince';category='Criolla';items=@(@('Lomo saltado',3200),@('Ají de gallina',2600),@('Arroz con pollo',2400),@('Chicha morada',800))},
 @{district='Lince';lat=-12.087;lon=-77.033;name='Masa Urbana Lince';category='Pizzas';items=@(@('Pizza margarita',2800),@('Pizza americana',3400),@('Pan al ajo',1200),@('Limonada',800))},
 @{district='La Victoria';lat=-12.071;lon=-77.022;name='Sazón Victoria';category='Criolla';items=@(@('Tallarín saltado',2600),@('Bistec a lo pobre',3000),@('Arroz chaufa',2200),@('Maracuyá',800))},
 @{district='La Victoria';lat=-12.075;lon=-77.019;name='Brasa del Barrio';category='Pollos';items=@(@('Cuarto de pollo con papas',2400),@('Medio pollo con papas',4400),@('Pollo entero familiar',7900),@('Ensalada fresca',1200))},
 @{district='Miraflores';lat=-12.122;lon=-77.030;name='Costa Fresca Miraflores';category='Marina';items=@(@('Ceviche clásico',3800),@('Arroz con mariscos',4200),@('Chicharrón de pescado',3600),@('Limonada frozen',1200))},
 @{district='Miraflores';lat=-12.117;lon=-77.033;name='Verde y Grano';category='Saludable';items=@(@('Bowl de quinua',2900),@('Ensalada de pollo',2800),@('Wrap de vegetales',2300),@('Jugo de naranja',1200))},
 @{district='San Isidro';lat=-12.098;lon=-77.035;name='Bistró del Olivar';category='Pastas';items=@(@('Fettuccine al pesto',3400),@('Lasagna de carne',3600),@('Ravioles de espinaca',3500),@('Tiramisú',1800))},
 @{district='San Isidro';lat=-12.093;lon=-77.027;name='Plancha San Isidro';category='Hamburguesas';items=@(@('Hamburguesa clásica',2600),@('Hamburguesa doble',3400),@('Papas artesanales',1200),@('Limonada de hierbabuena',1000))}
)
$results=@($state.restaurants)
foreach($d in $definitions){
 $name="$($d.name) - DEMO"
 $existing=$businesses|ForEach-Object{$_}|Where-Object name -eq $name|Select-Object -First 1
 if($existing){Write-Host "Ya existe: $name";continue}
 Write-Host "Creando $name ($($d.district))..."
 $business=Api '/merchants' 'POST' @{name=$name;taxId=('DEMO-'+[guid]::NewGuid().ToString('N').Substring(0,12));bankAccount='DEMO SIN CUENTA REAL';documentIds=@((Document $mt))} $mt
 Api "/merchants/$($business.id)/decision" 'PUT' @{status='approved';reason='Restaurante ficticio para demostración local'} $at|Out-Null
 $branch=Api "/merchants/$($business.id)/branches" 'POST' @{name="$name · $($d.district)";location=@{latitude=$d.lat;longitude=$d.lon};minimumOrder=1500;preparationMinutes=20;active=$true;hours=@(0..6|ForEach-Object{@{day=$_;fromMinute=0;toMinute=1440}});pausedUntil=$null} $mt
 foreach($item in $d.items){
  Api "/merchant/branches/$($branch.id)/products" 'POST' @{name=$item[0];category=$d.category;description="Producto de demostración de $($d.district). Precio ficticio; no hay venta real.";imageUrl=$null;allergens=@();price=$item[1];stock=100;available=$true;modifiers=@()} $mt|Out-Null
 }
 $results+=@{name=$name;district=$d.district;merchantId=$business.id;branchId=$branch.id;latitude=$d.lat;longitude=$d.lon;products=4}
 @{restaurants=$results;merchantEmail=$previous.merchantEmail;note='Datos ficticios; coordenadas aproximadas; horario 24/7 solo para pruebas.'}|ConvertTo-Json -Depth 10|Set-Content $statePath -Encoding utf8
}
foreach($d in $definitions|Group-Object district|ForEach-Object{$_.Group[0]}){
 $found=Api "/catalog/search?latitude=$($d.lat)&longitude=$($d.lon)&radius=10&onlyOpen=true&text=DEMO"
 if(@($found|ForEach-Object{$_}).Count -lt 8){throw "Catálogo incompleto desde $($d.district)."}
}
Write-Host 'Carga verificada: 8 restaurantes DEMO, 32 productos, visibles desde los cuatro distritos.'
