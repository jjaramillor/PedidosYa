import 'dart:async';
import 'package:flutter/material.dart';
import 'package:geolocator/geolocator.dart';
import 'package:url_launcher/url_launcher.dart';
import 'package:uuid/uuid.dart';
import 'package:image_picker/image_picker.dart';
import 'api.dart';
import 'offers.dart';
import 'customer.dart';
import 'presentation.dart';

void main() => runApp(const DeliveryApp());

class DeliveryApp extends StatelessWidget {
  const DeliveryApp({super.key});
  @override
  Widget build(BuildContext context) => MaterialApp(
      title: 'Delivery', theme: deliveryTheme(), home: const CourierHome());
}

class CourierHome extends StatefulWidget {
  const CourierHome({super.key, this.api, this.now, this.positionProvider});
  final DeliveryApi? api;
  final DateTime Function()? now;
  final Future<Position> Function()? positionProvider;
  @override
  State<CourierHome> createState() => _CourierHomeState();
}

class _CourierHomeState extends State<CourierHome> with WidgetsBindingObserver {
  late final api = widget.api ??
      DeliveryApi(
          baseUrl: const String.fromEnvironment('API_URL',
              defaultValue: 'https://localhost:8080'));
  DateTime get now => widget.now?.call() ?? DateTime.now();
  final scroll = ScrollController();
  final email = TextEditingController();
  final code = TextEditingController();
  final pin = TextEditingController();
  final reason = TextEditingController();
  String? challenge;
  bool authenticated = false;
  int destination = 0;
  bool busy = false;
  String? error;
  final displayName = TextEditingController();
  bool registering = false;
  bool acceptedPolicies = false;
  List<dynamic> offers = [];
  List<dynamic> deliveries = [];
  Map<String, dynamic>? order;
  Map<String, dynamic>? fulfillment;
  Timer? gps;
  Timer? offerExpiry;
  String? trackedOrder;
  bool available = false;
  bool cashCollected = false;
  bool supportRequested = false;
  String? supportMessage;
  String vehicle = 'bicycle';
  final documents = <String>[];
  String? evidence;
  dynamic earnings;
  String? userId;
  Future<String?> photograph(ImageSource source) async {
    final image = await ImagePicker()
        .pickImage(source: source, maxWidth: 1600, imageQuality: 85);
    if (image == null) return null;
    final bytes = await image.readAsBytes();
    final png = bytes.length >= 4 &&
        bytes[0] == 137 &&
        bytes[1] == 80 &&
        bytes[2] == 78 &&
        bytes[3] == 71;
    return api.upload(image.name, bytes, png ? 'image/png' : 'image/jpeg');
  }

  Future<void> apply() async {
    await api.call('/couriers/apply',
        method: 'POST', body: {'vehicle': vehicle, 'documentIds': documents});
  }

  Future<void> report() async {
    userId ??= (await api.call('/identity/me'))['id'] as String;
    earnings = await api.call('/statements/courier/$userId');
  }

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    offerExpiry = Timer.periodic(const Duration(seconds: 1), (_) {
      if (!mounted || offers.isEmpty) return;
      final active = actionableOffers(offers, now);
      if (active.length != offers.length) setState(() => offers = active);
    });
    run(restoreSession);
  }

  @override
  void dispose() {
    offerExpiry?.cancel();
    gps?.cancel();
    WidgetsBinding.instance.removeObserver(this);
    api.dispose();
    scroll.dispose();
    email.dispose();
    code.dispose();
    pin.dispose();
    reason.dispose();
    displayName.dispose();
    super.dispose();
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    if (state != AppLifecycleState.resumed) {
      gps?.cancel();
      gps = null;
      trackedOrder = null;
    }
  }

  Future<void> restoreSession() async {
    try {
      await api.restore();
      authenticated = true;
      await reload();
    } on DeliveryApiException catch (error) {
      if (error.statusCode != 401) rethrow;
      authenticated = false;
    }
  }

  Future<void> run(Future<void> Function() work) async {
    if (busy) return;
    setState(() {
      busy = true;
      error = null;
    });
    try {
      await work();
    } catch (exception) {
      if (mounted) {
        if (exception is DeliveryApiException && exception.statusCode == 401) {
          gps?.cancel();
          gps = null;
          trackedOrder = null;
          authenticated = false;
          available = false;
          order = null;
          fulfillment = null;
          offers = [];
          deliveries = [];
          earnings = null;
          userId = null;
          challenge = null;
          pin.clear();
          reason.clear();
          evidence = null;
          cashCollected = false;
          supportRequested = false;
          supportMessage = null;
          documents.clear();
          code.clear();
          destination = 0;
        }
        setState(
            () => error = exception.toString().replaceFirst('Exception: ', ''));
      }
    } finally {
      if (mounted) setState(() => busy = false);
    }
  }

  Future<Position> position() async {
    if (widget.positionProvider != null) return widget.positionProvider!();
    if (!await Geolocator.isLocationServiceEnabled()) {
      throw Exception('Activa la ubicación del dispositivo.');
    }
    var permission = await Geolocator.checkPermission();
    if (permission == LocationPermission.denied) {
      permission = await Geolocator.requestPermission();
    }
    if (permission == LocationPermission.denied ||
        permission == LocationPermission.deniedForever) {
      throw Exception('Se necesita permiso de ubicación durante el servicio.');
    }
    return Geolocator.getCurrentPosition(
        locationSettings: const LocationSettings(
            accuracy: LocationAccuracy.high, timeLimit: Duration(seconds: 15)));
  }

  Future<void> reload() async {
    if (available) {
      final point = await position();
      final location = {
        'latitude': point.latitude,
        'longitude': point.longitude
      };
      final zone = await api.call('/coverage', method: 'POST', body: location);
      await api.call('/couriers/availability', method: 'PUT', body: {
        'available': true,
        'zoneId': zone['zoneId'],
        'location': location
      });
    }
    offers = actionableOffers(
        await api.call('/couriers/offers') as List<dynamic>, now);
    deliveries = await api.call('/couriers/deliveries');
  }

  Future<void> login() async {
    dynamic body = {'email': email.text.trim(), 'deviceName': 'App móvil'};
    if (registering) {
      if (!acceptedPolicies) {
        throw Exception('Acepta los términos y la política de privacidad.');
      }
      final policies = await api.call('/identity/policies');
      body = {
        'email': email.text.trim(),
        'displayName': displayName.text.trim(),
        'deviceName': 'App móvil',
        'purpose': 'register',
        'acceptTerms': true,
        'acceptPrivacy': true,
        'termsVersion': policies['termsVersion'],
        'privacyVersion': policies['privacyVersion']
      };
    }
    final result = await api.call(
        registering ? '/identity/otp/request' : '/identity/login',
        method: 'POST',
        body: body);
    challenge = result['challengeId'];
  }

  Future<void> verify() async {
    await api.verify(challenge!, code.text);
    authenticated = true;
    challenge = null;
    code.clear();
    await reload();
  }

  Future<void> availability() async {
    final point = await position();
    final location = {'latitude': point.latitude, 'longitude': point.longitude};
    final zone = await api.call('/coverage', method: 'POST', body: location);
    await api.call('/couriers/availability', method: 'PUT', body: {
      'available': !available,
      'zoneId': zone['zoneId'],
      'location': location
    });
    available = !available;
  }

  Future<void> open(String id) async {
    if (order?['id'] != id) {
      gps?.cancel();
      gps = null;
      trackedOrder = null;
      evidence = null;
      cashCollected = false;
      supportRequested = false;
      supportMessage = null;
      pin.clear();
      reason.clear();
    }
    order = Map<String, dynamic>.from(await api.call('/orders/$id'));
    fulfillment = Map<String, dynamic>.from(await api.call('/deliveries/$id'));
    if (scroll.hasClients) scroll.jumpTo(0);
  }

  Future<void> act(String state) async {
    if (reason.text.trim().isEmpty) {
      throw Exception('Indica el motivo de la acción.');
    }
    await api
        .call('/deliveries/${order!['id']}/actions', method: 'POST', body: {
      'operationId': const Uuid().v4(),
      'state': state,
      'pin': pin.text.isEmpty ? null : pin.text,
      'evidenceId': state == 'delivered' ? evidence : null,
      'cashCollected': cashCollected,
      'reason': reason.text.trim()
    });
    if (['delivered', 'returned', 'cancelled'].contains(state)) {
      gps?.cancel();
      gps = null;
      trackedOrder = null;
      evidence = null;
    }
    pin.clear();
    await open(order!['id']);
    await reload();
  }

  Future<void> requestSupport() async {
    final description = reason.text.trim();
    if (description.isEmpty) {
      throw Exception('Describe el problema en Motivo / observación.');
    }
    await api.call('/support/cases', method: 'POST', body: {
      'orderId': order!['id'],
      'subject': 'Ayuda con la entrega',
      'description': description,
      'evidenceIds': <String>[]
    });
    supportRequested = true;
    supportMessage = 'Solicitud pendiente de revisión.';
  }

  Future<void> loadSupportResponse() async {
    final cases = await api.call('/support/cases') as List<dynamic>;
    final matching = cases.where((item) => item['orderId'] == order!['id']);
    if (matching.isEmpty) {
      supportMessage = 'No hay solicitudes de ayuda para esta entrega.';
      return;
    }
    final detail = await api.call('/support/cases/${matching.first['id']}');
    final state = detail['case']['state'] as String;
    final label = {
          'open': 'Pendiente de revisión',
          'investigating': 'En investigación',
          'resolved': 'Resuelta',
          'closed': 'Cerrada'
        }[state] ??
        'En revisión';
    final resolution = (detail['resolution'] as String?)?.trim();
    supportRequested = !['resolved', 'closed'].contains(state);
    supportMessage = 'Última solicitud: $label';
    if (resolution != null && resolution.isNotEmpty) {
      supportMessage = '$supportMessage\n$resolution';
    }
  }

  Future<void> sendLocation(String id) async {
    final fresh = await api.call('/deliveries/$id');
    if (!['assigned', 'arrived', 'picked_up', 'in_transit', 'incident']
        .contains(fresh['state'])) {
      gps?.cancel();
      gps = null;
      trackedOrder = null;
      return;
    }
    final point = await position();
    await api.call('/deliveries/$id/location',
        method: 'POST',
        body: {'latitude': point.latitude, 'longitude': point.longitude});
  }

  Future<void> startTracking() async {
    final id = order!['id'] as String;
    await sendLocation(id);
    trackedOrder = id;
    gps?.cancel();
    gps = Timer.periodic(const Duration(seconds: 20), (_) async {
      if (busy || trackedOrder != id) return;
      try {
        await sendLocation(id);
      } catch (exception) {
        if (busy || trackedOrder != id) return;
        gps?.cancel();
        gps = null;
        trackedOrder = null;
        if (mounted) setState(() => error = exception.toString());
      }
      if (mounted) setState(() {});
    });
  }

  Future<void> navigate() async {
    final quote = order!['quote'];
    final pickup = ['assigned', 'arrived'].contains(fulfillment!['state']);
    final destination =
        pickup ? quote['branch']['location'] : quote['address']['location'];
    final uri = Uri.https('www.google.com', '/maps/dir/', {
      'api': '1',
      'destination': '${destination['latitude']},${destination['longitude']}',
      'travelmode': 'driving'
    });
    if (!await launchUrl(uri, mode: LaunchMode.externalApplication)) {
      throw Exception('No se pudo abrir la navegación.');
    }
  }

  @override
  Widget build(BuildContext context) => Scaffold(
      appBar: AppBar(
          title: Text(authenticated ? 'delivery · repartidor' : 'delivery'),
          actions: [
            if (authenticated)
              IconButton(
                  tooltip: 'Cerrar sesión',
                  icon: const Icon(Icons.logout),
                  onPressed: busy
                      ? null
                      : () => run(() async {
                            gps?.cancel();
                            gps = null;
                            trackedOrder = null;
                            try {
                              await api.logout();
                            } finally {
                              authenticated = false;
                              available = false;
                              order = null;
                              fulfillment = null;
                              offers = [];
                              deliveries = [];
                              earnings = null;
                              userId = null;
                              evidence = null;
                              cashCollected = false;
                              supportRequested = false;
                              supportMessage = null;
                              documents.clear();
                              email.clear();
                              code.clear();
                              displayName.clear();
                              pin.clear();
                              reason.clear();
                              challenge = null;
                            }
                          }))
          ]),
      bottomNavigationBar: authenticated && order == null
          ? NavigationBar(
              selectedIndex: destination,
              onDestinationSelected: busy
                  ? null
                  : (value) => setState(() {
                        destination = value;
                        if (scroll.hasClients) scroll.jumpTo(0);
                      }),
              destinations: const [
                NavigationDestination(
                    icon: Icon(Icons.delivery_dining_outlined),
                    selectedIcon: Icon(Icons.delivery_dining),
                    label: 'Entregas'),
                NavigationDestination(
                    icon: Icon(Icons.account_balance_wallet_outlined),
                    label: 'Ganancias'),
                NavigationDestination(
                    icon: Icon(Icons.person_outline), label: 'Perfil')
              ],
            )
          : null,
      body: SafeArea(
          top: false,
          child: ListView(
              controller: scroll,
              padding: const EdgeInsets.fromLTRB(20, 12, 20, 24),
              children: [
                if (busy) const LinearProgressIndicator(),
                if (error != null)
                  Padding(
                      padding: const EdgeInsets.symmetric(vertical: 16),
                      child: Text(error!,
                          style: TextStyle(
                              color: Theme.of(context).colorScheme.error))),
                if (!authenticated) ...[
                  if (error != null)
                    OutlinedButton(
                        onPressed: busy ? null : () => run(restoreSession),
                        child: const Text('Reintentar sesión guardada')),
                  const IntroPanel(
                      eyebrow: 'BIENVENIDO A DELIVERY',
                      title: 'Tu próximo destino empieza aquí.',
                      description:
                          'Ingresa con el código de tu correo. Desde tu cuenta puedes repartir o pedir comida.',
                      icon: Icons.near_me_outlined),
                  TextField(
                      controller: email,
                      decoration: const InputDecoration(labelText: 'Correo'),
                      keyboardType: TextInputType.emailAddress),
                  if (registering && challenge == null) ...[
                    TextField(
                        controller: displayName,
                        decoration:
                            const InputDecoration(labelText: 'Nombre visible')),
                    CheckboxListTile(
                        title: const Text(
                            'Acepto los términos y la política de privacidad'),
                        value: acceptedPolicies,
                        onChanged: busy
                            ? null
                            : (value) => setState(
                                () => acceptedPolicies = value == true))
                  ],
                  if (challenge == null)
                    TextButton(
                        onPressed: busy
                            ? null
                            : () => setState(() => registering = !registering),
                        child: Text(registering
                            ? 'Ya tengo cuenta'
                            : 'Crear una cuenta')),
                  if (challenge == null)
                    FilledButton(
                        onPressed: busy ? null : () => run(login),
                        child: const Text('Enviar código'))
                  else ...[
                    TextField(
                        controller: code,
                        decoration:
                            const InputDecoration(labelText: 'Código OTP'),
                        keyboardType: TextInputType.number,
                        maxLength: 6),
                    FilledButton(
                        onPressed: busy ? null : () => run(verify),
                        child: const Text('Confirmar'))
                  ]
                ],
                if (authenticated) ...[
                  if (order == null) ...[
                    if (destination == 2) ...[
                      const SectionTitle('Tu perfil',
                          subtitle:
                              'Afiliación y acceso al espacio de cliente.'),
                      FilledButton(
                          onPressed: busy
                              ? null
                              : () {
                                  gps?.cancel();
                                  gps = null;
                                  trackedOrder = null;
                                  Navigator.push(
                                      context,
                                      MaterialPageRoute(
                                          builder: (_) =>
                                              CustomerArea(api: api)));
                                },
                          child: const Text('Pedir comida como cliente')),
                      ExpansionTile(
                          title: const Text('Solicitud de alta'),
                          children: [
                            DropdownButton<String>(
                                value: vehicle,
                                items: const [
                                  DropdownMenuItem(
                                      value: 'bicycle',
                                      child: Text('Bicicleta')),
                                  DropdownMenuItem(
                                      value: 'motorbike', child: Text('Moto')),
                                  DropdownMenuItem(
                                      value: 'car', child: Text('Auto'))
                                ],
                                onChanged: (value) =>
                                    setState(() => vehicle = value!)),
                            OutlinedButton(
                                onPressed: busy
                                    ? null
                                    : () => run(() async {
                                          final id = await photograph(
                                              ImageSource.gallery);
                                          if (id != null) documents.add(id);
                                        }),
                                child: Text(
                                    'Adjuntar documento (${documents.length})')),
                            FilledButton(
                                onPressed: busy || documents.isEmpty
                                    ? null
                                    : () => run(apply),
                                child: const Text('Enviar solicitud'))
                          ]),
                    ],
                    if (destination == 1) ...[
                      const SectionTitle('Tus ganancias',
                          subtitle:
                              'Consulta saldos, movimientos y liquidaciones.'),
                      OutlinedButton(
                          onPressed: busy ? null : () => run(report),
                          child: const Text('Ver ganancias y liquidaciones')),
                      if (earnings != null) ...[
                        Text(
                            'Saldo pendiente: S/ ${(earnings['pending'] / 100).toStringAsFixed(2)}'),
                        for (final movement in earnings['movements'])
                          ListTile(
                              title: Text(movement['kind'] == 'incentive'
                                  ? 'Incentivo'
                                  : movement['kind'] == 'delivery'
                                      ? 'Ganancia de entrega'
                                      : movement['kind'] == 'capture'
                                          ? 'Efectivo recibido para rendir'
                                          : 'Ajuste financiero'),
                              subtitle: Text(movement['at']),
                              trailing: Text(
                                  'S/ ${(movement['amount'] / 100).toStringAsFixed(2)}')),
                        for (final settlement in earnings['settlements'])
                          ListTile(
                              title: Text(
                                  'Liquidación: S/ ${(settlement['amount'] / 100).toStringAsFixed(2)}'),
                              subtitle: Text(settlement['paidAt'] == null
                                  ? 'Pendiente de pago'
                                  : 'Pagada'))
                      ],
                    ],
                    if (destination == 0) ...[
                      IntroPanel(
                          eyebrow: 'TU JORNADA',
                          title:
                              available ? 'Listo para salir.' : 'A tu ritmo.',
                          description: available
                              ? 'Estás disponible para recibir ofertas. Revisa los pedidos antes de aceptar.'
                              : 'Activa tu disponibilidad cuando estés listo para recibir pedidos.',
                          icon: Icons.delivery_dining),
                      FilledButton(
                          onPressed: busy ? null : () => run(availability),
                          child: Text(available
                              ? 'Dejar de recibir ofertas'
                              : 'Disponible para pedidos')),
                      OutlinedButton(
                          onPressed: busy ? null : () => run(reload),
                          child: const Text('Actualizar ofertas y entregas')),
                      const SizedBox(height: 20),
                      const SectionTitle('Ofertas'),
                      if (offers.isEmpty)
                        const Text(
                            'No hay ofertas disponibles. Actualiza para consultar nuevas oportunidades.'),
                      for (final offer in offers)
                        Card(
                            child: Padding(
                                padding: const EdgeInsets.all(16),
                                child: Column(
                                    crossAxisAlignment:
                                        CrossAxisAlignment.start,
                                    children: [
                                      Text('Pedido ${offer['orderId']}'),
                                      Text(
                                          'Oferta disponible hasta ${offer['expiresAt']}'),
                                      Row(children: [
                                        TextButton(
                                            onPressed: busy
                                                ? null
                                                : () => run(() async {
                                                      await api.call(
                                                          '/couriers/offers/${offer['id']}/decision',
                                                          method: 'POST',
                                                          body: {
                                                            'accept': true
                                                          });
                                                      await reload();
                                                    }),
                                            child: const Text('Aceptar')),
                                        TextButton(
                                            onPressed: busy
                                                ? null
                                                : () => run(() async {
                                                      await api.call(
                                                          '/couriers/offers/${offer['id']}/decision',
                                                          method: 'POST',
                                                          body: {
                                                            'accept': false
                                                          });
                                                      await reload();
                                                    }),
                                            child: const Text('Rechazar'))
                                      ])
                                    ]))),
                      const SectionTitle('Mis entregas'),
                      if (deliveries.isEmpty)
                        const Text(
                            'Tus entregas aparecerán aquí cuando aceptes un pedido.'),
                      for (final item in deliveries)
                        ListTile(
                            title: Text('Pedido $item'),
                            subtitle: const Text('Ver estado y dirección'),
                            trailing: const Icon(Icons.chevron_right),
                            onTap: busy
                                ? null
                                : () => run(() => open(item as String))),
                    ],
                  ],
                  if (order != null && fulfillment != null) ...[
                    OutlinedButton.icon(
                        onPressed: busy
                            ? null
                            : () => setState(() {
                                  order = null;
                                  fulfillment = null;
                                  error = null;
                                  if (scroll.hasClients) scroll.jumpTo(0);
                                }),
                        icon: const Icon(Icons.arrow_back),
                        label: const Text('Volver a ofertas y entregas')),
                    Text('Pedido ${order!['id']}'),
                    Text(order!['quote']['branch']['name'],
                        style: const TextStyle(
                            fontSize: 22, fontWeight: FontWeight.bold)),
                    Text('Destino: ${order!['quote']['address']['street']}'),
                    Text(
                        'Estado: ${deliveryStateLabel(fulfillment!['state'] as String)}'),
                    OutlinedButton(
                        onPressed:
                            busy ? null : () => run(() => open(order!['id'])),
                        child: const Text('Actualizar estado de entrega')),
                    OutlinedButton(
                        onPressed: busy ? null : () => run(navigate),
                        child: const Text('Abrir navegación')),
                    TextField(
                        controller: reason,
                        decoration: const InputDecoration(
                            labelText: 'Motivo / observación')),
                    TextField(
                        controller: pin,
                        decoration: const InputDecoration(
                            labelText: 'Código de retiro o entrega'),
                        keyboardType: TextInputType.number,
                        maxLength: 6),
                    if (['in_transit', 'incident']
                        .contains(fulfillment!['state']))
                      OutlinedButton(
                          onPressed: busy
                              ? null
                              : () => run(() async {
                                    evidence =
                                        await photograph(ImageSource.camera);
                                  }),
                          child: Text(evidence == null
                              ? 'Adjuntar fotografía de entrega'
                              : 'Fotografía adjunta')),
                    if (order!['method'] == 'cash' &&
                        ['in_transit', 'incident']
                            .contains(fulfillment!['state']))
                      CheckboxListTile(
                          title: Text(
                              'Recibí S/ ${(order!['quote']['total'] / 100).toStringAsFixed(2)} en efectivo'),
                          value: cashCollected,
                          onChanged: busy
                              ? null
                              : (value) => setState(
                                  () => cashCollected = value == true)),
                    for (final action in switch (fulfillment!['state']) {
                      'assigned' => ['arrived'],
                      'arrived' => ['picked_up'],
                      'picked_up' => ['in_transit'],
                      'in_transit' => ['delivered', 'incident'],
                      'incident' => ['in_transit', 'delivered', 'returned'],
                      _ => <String>[]
                    })
                      FilledButton(
                          onPressed: busy ? null : () => run(() => act(action)),
                          child: Text({
                            'arrived': 'Llegué al restaurante',
                            'picked_up': 'Confirmar retiro',
                            'in_transit': 'Iniciar ruta',
                            'delivered': 'Confirmar entrega',
                            'incident': 'Reportar incidencia',
                            'returned': 'Confirmar devolución'
                          }[action]!)),
                    if ([
                      'assigned',
                      'arrived',
                      'picked_up',
                      'in_transit',
                      'incident'
                    ].contains(fulfillment!['state']))
                      OutlinedButton(
                          onPressed: busy
                              ? null
                              : () => run(() async {
                                    if (gps != null) {
                                      gps?.cancel();
                                      gps = null;
                                      trackedOrder = null;
                                    } else {
                                      await startTracking();
                                    }
                                  }),
                          child: Text(gps == null
                              ? 'Compartir ubicación durante esta entrega'
                              : 'Detener ubicación')),
                    const Text(
                        'La ubicación se comparte solo con la app abierta y durante una entrega activa.'),
                    OutlinedButton.icon(
                        onPressed: busy || supportRequested
                            ? null
                            : () => run(requestSupport),
                        icon: const Icon(Icons.support_agent),
                        label: Text(supportRequested
                            ? 'Solicitud enviada a soporte'
                            : 'Solicitar ayuda con esta entrega')),
                    OutlinedButton(
                        onPressed: busy ? null : () => run(loadSupportResponse),
                        child: const Text('Consultar respuesta de soporte')),
                    if (supportMessage != null)
                      Padding(
                          padding: const EdgeInsets.symmetric(vertical: 12),
                          child: Text(supportMessage!,
                              semanticsLabel: supportMessage))
                  ]
                ]
              ])));
}
