import 'dart:async';
import 'package:geolocator/geolocator.dart';
import 'package:flutter/material.dart';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:delivery_mobile/main.dart';
import 'package:delivery_mobile/api.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  setUp(() {
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(
            const MethodChannel('plugins.it_nomads.com/flutter_secure_storage'),
            (call) async => null);
  });
  testWidgets('An unauthenticated courier must sign in before seeing offers',
      (tester) async {
    await tester.pumpWidget(const DeliveryApp());
    await tester.pumpAndSettle();
    expect(find.text('Enviar código'), findsOneWidget);
    expect(find.text('Ofertas'), findsNothing);
    expect(find.text('Disponible para pedidos'), findsNothing);
  });
  testWidgets(
      'Opening a delivery exposes actions without scrolling past history',
      (tester) async {
    await tester.pumpWidget(MaterialApp(home: CourierHome(api: CourierApi())));
    await tester.pumpAndSettle();
    await tester.scrollUntilVisible(find.text('Pedido order-1'), 300);
    await tester.tap(find.text('Pedido order-1'));
    await tester.pumpAndSettle();
    expect(find.text('Restaurante de prueba'), findsOneWidget);
    expect(find.text('Llegué al restaurante'), findsOneWidget);
    expect(find.text('Solicitud de alta'), findsNothing);
    await tester.tap(find.text('Llegué al restaurante'));
    await tester.pumpAndSettle();
    expect(find.text('Indica el motivo de la acción.'), findsOneWidget);
    await tester.enterText(
        find.widgetWithText(TextField, 'Motivo / observación'),
        'Llegada de prueba');
    await tester.tap(find.text('Llegué al restaurante'));
    await tester.pumpAndSettle();
    expect(find.text('Confirmar retiro'), findsOneWidget);
    await tester.tap(find.text('Volver a ofertas y entregas'));
    await tester.pumpAndSettle();
    expect(find.text('Solicitud de alta'), findsNothing);
    await tester.tap(find.text('Perfil'));
    await tester.pumpAndSettle();
    expect(find.text('Solicitud de alta'), findsOneWidget);
    expect(find.text('Llegué al restaurante'), findsNothing);
  });
  testWidgets(
      'A rejected runtime session returns to OTP without retaining delivery controls',
      (tester) async {
    final api = RevokedCourierApi();
    await tester.pumpWidget(MaterialApp(home: CourierHome(api: api)));
    await tester.pumpAndSettle();
    api.revoked = true;
    await tester.ensureVisible(find.text('Actualizar ofertas y entregas'));
    await tester.tap(find.text('Actualizar ofertas y entregas'));
    await tester.pumpAndSettle();
    await tester.ensureVisible(find.text('Enviar código'));
    expect(find.text('Enviar código'), findsOneWidget);
    expect(find.text('Ofertas'), findsNothing);
    expect(find.byType(NavigationBar), findsNothing);
    expect(find.text('Reintentar sesión guardada'), findsOneWidget);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('A saved session can be retried after connectivity returns',
      (tester) async {
    final api = RecoveringCourierApi();
    await tester.pumpWidget(MaterialApp(home: CourierHome(api: api)));
    await tester.pumpAndSettle();
    expect(find.text('Ofertas'), findsNothing);
    expect(find.text('Reintentar sesión guardada'), findsOneWidget);
    await tester.tap(find.text('Reintentar sesión guardada'));
    await tester.pumpAndSettle();
    expect(api.attempts, 2);
    expect(find.text('Ofertas'), findsOneWidget);
    expect(find.text('Enviar código'), findsNothing);
  });

  testWidgets('A lost response is resolved by reading state without replaying',
      (tester) async {
    final api = CourierApi()..loseActionResponse = true;
    await tester.pumpWidget(MaterialApp(home: CourierHome(api: api)));
    await tester.pumpAndSettle();
    await tester.scrollUntilVisible(find.text('Pedido order-1'), 300);
    await tester.tap(find.text('Pedido order-1'));
    await tester.pumpAndSettle();
    await tester.enterText(
        find.widgetWithText(TextField, 'Motivo / observación'), 'Prueba');
    await tester.ensureVisible(find.text('Llegué al restaurante'));
    await tester.tap(find.text('Llegué al restaurante'));
    await tester.pumpAndSettle();
    expect(api.actions, 1);
    await tester.scrollUntilVisible(find.text('Respuesta perdida'), -150,
        scrollable: find.byType(Scrollable).first);
    await tester.pumpAndSettle();
    expect(find.text('Respuesta perdida'), findsOneWidget);
    await tester.ensureVisible(find.text('Actualizar estado de entrega'));
    await tester.tap(find.text('Actualizar estado de entrega'));
    await tester.pumpAndSettle();
    expect(find.text('Confirmar retiro'), findsOneWidget);
    expect(api.actions, 1);
  });
  testWidgets('An offer disappears when it expires without a manual reload',
      (tester) async {
    var now = DateTime.now();
    await tester.pumpWidget(MaterialApp(
        home: CourierHome(api: ExpiringOfferApi(), now: () => now)));
    await tester.pumpAndSettle();
    expect(find.text('Aceptar'), findsOneWidget);
    now = now.add(const Duration(seconds: 3));
    await tester.pump(const Duration(seconds: 3));
    await tester.pump();
    expect(find.text('Aceptar'), findsNothing);
    await tester.pumpWidget(const SizedBox());
  });
  testWidgets(
      'A courier can open support for the selected delivery without sharing its PIN',
      (tester) async {
    final api = CourierApi();
    await tester.pumpWidget(MaterialApp(home: CourierHome(api: api)));
    await tester.pumpAndSettle();
    await tester.scrollUntilVisible(find.text('Pedido order-1'), 300);
    await tester.tap(find.text('Pedido order-1'));
    await tester.pumpAndSettle();
    await tester.enterText(
        find.widgetWithText(TextField, 'Motivo / observación'),
        'PIN bloqueado: necesito asistencia');
    await tester.enterText(
        find.widgetWithText(TextField, 'Código de retiro o entrega'), '123456');
    tester.testTextInput.hide();
    await tester.pumpAndSettle();
    await tester.scrollUntilVisible(
        find.text('Solicitar ayuda con esta entrega'), 150,
        scrollable: find.byType(Scrollable).first);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Solicitar ayuda con esta entrega'));
    await tester.pumpAndSettle();
    expect(api.supportBody, {
      'orderId': 'order-1',
      'subject': 'Ayuda con la entrega',
      'description': 'PIN bloqueado: necesito asistencia',
      'evidenceIds': <String>[]
    });
    expect(find.text('Solicitud enviada a soporte'), findsOneWidget);
    final button = tester.widget<OutlinedButton>(find.ancestor(
        of: find.text('Solicitud enviada a soporte'),
        matching:
            find.byWidgetPredicate((widget) => widget is OutlinedButton)));
    expect(button.onPressed, isNull);
    await tester.pumpWidget(const SizedBox());
  });
  testWidgets(
      'A late GPS rejection cannot turn completed delivery into an error',
      (tester) async {
    final api = DelayedLocationApi()..state = 'in_transit';
    await tester.pumpWidget(MaterialApp(
        home: CourierHome(
            api: api,
            positionProvider: () async => Position(
                latitude: -12.04,
                longitude: -77.04,
                timestamp: DateTime.now(),
                accuracy: 1,
                altitude: 0,
                altitudeAccuracy: 1,
                heading: 0,
                headingAccuracy: 1,
                speed: 0,
                speedAccuracy: 1))));
    await tester.pumpAndSettle();
    await tester.scrollUntilVisible(find.text('Pedido order-1'), 300);
    await tester.tap(find.text('Pedido order-1'));
    await tester.pumpAndSettle();
    await tester.enterText(
        find.widgetWithText(TextField, 'Motivo / observación'), 'Prueba GPS');
    tester.testTextInput.hide();
    await tester.pumpAndSettle();
    await tester.scrollUntilVisible(
        find.text('Compartir ubicación durante esta entrega'), 150,
        scrollable: find.byType(Scrollable).first);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Compartir ubicación durante esta entrega'));
    await tester.pumpAndSettle();
    await tester.pump(const Duration(seconds: 20));
    await tester.pump();
    expect(api.locations, 2);
    await tester.ensureVisible(find.text('Confirmar entrega'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Confirmar entrega'));
    await tester.pumpAndSettle();
    api.pending.completeError(
        DeliveryApiException(409, 'El seguimiento no está activo.'));
    await tester.pumpAndSettle();
    expect(find.text('Estado: Entregado'), findsOneWidget);
    expect(find.textContaining('El seguimiento no está activo'), findsNothing);
    await tester.pump(const Duration(seconds: 21));
    expect(api.locations, 2);
    await tester.pumpWidget(const SizedBox());
  });
  testWidgets(
      'Support resolution is selected for the open delivery from stored cases',
      (tester) async {
    final api = CourierApi()
      ..supportCases = [
        {'id': 'unrelated-case', 'orderId': 'another-order'},
        {'id': 'case-current', 'orderId': 'order-1'}
      ]
      ..supportDetail = {
        'case': {'state': 'resolved'},
        'resolution': 'Comercio contactado; continúa con el retiro.'
      };
    await tester.pumpWidget(MaterialApp(home: CourierHome(api: api)));
    await tester.pumpAndSettle();
    await tester.scrollUntilVisible(find.text('Pedido order-1'), 300);
    await tester.tap(find.text('Pedido order-1'));
    await tester.pumpAndSettle();
    await tester.scrollUntilVisible(
        find.text('Consultar respuesta de soporte'), 150,
        scrollable: find.byType(Scrollable).first);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Consultar respuesta de soporte'));
    await tester.pumpAndSettle();
    expect(
        find.text(
            'Última solicitud: Resuelta\nComercio contactado; continúa con el retiro.'),
        findsOneWidget);
    await tester.pumpWidget(const SizedBox());
  });

  testWidgets('An order without support cases shows a clear empty state',
      (tester) async {
    await tester.pumpWidget(MaterialApp(home: CourierHome(api: CourierApi())));
    await tester.pumpAndSettle();
    await tester.scrollUntilVisible(find.text('Pedido order-1'), 300);
    await tester.tap(find.text('Pedido order-1'));
    await tester.pumpAndSettle();
    await tester.scrollUntilVisible(
        find.text('Consultar respuesta de soporte'), 150,
        scrollable: find.byType(Scrollable).first);
    await tester.pumpAndSettle();
    await tester.tap(find.text('Consultar respuesta de soporte'));
    await tester.pumpAndSettle();
    expect(find.text('No hay solicitudes de ayuda para esta entrega.'),
        findsOneWidget);
    expect(find.text('Solicitud enviada a soporte'), findsNothing);
    await tester.pumpWidget(const SizedBox());
  });
}

class CourierApi extends DeliveryApi {
  CourierApi() : super(baseUrl: 'https://example.test');
  String state = 'assigned';
  bool loseActionResponse = false;
  int actions = 0;
  Object? supportBody;
  List<dynamic> supportCases = [];
  dynamic supportDetail;
  @override
  Future<void> restore() async {}
  @override
  Future<dynamic> call(String path,
      {String method = 'GET', Object? body, bool retry = true}) async {
    if (path == '/support/cases' && method == 'GET') return supportCases;
    if (path == '/support/cases/case-current' && method == 'GET') {
      return supportDetail;
    }
    if (path == '/support/cases' && method == 'POST') {
      supportBody = body;
      return {'id': 'case-1'};
    }
    if (path == '/deliveries/order-1/actions' && method == 'POST') {
      actions++;
      state = (body as Map)['state'] as String;
      if (loseActionResponse) throw Exception('Respuesta perdida');
      return null;
    }
    return switch (path) {
      '/couriers/offers' => <dynamic>[],
      '/couriers/deliveries' => ['order-1'],
      '/orders/order-1' => {
          'id': 'order-1',
          'method': 'card',
          'quote': {
            'branch': {'name': 'Restaurante de prueba'},
            'address': {'street': 'Dirección de prueba'}
          }
        },
      '/deliveries/order-1' => {'state': state},
      _ => throw StateError('Unexpected request: $path')
    };
  }
}

class RecoveringCourierApi extends CourierApi {
  int attempts = 0;
  @override
  Future<void> restore() async {
    attempts++;
    if (attempts == 1) throw Exception('Sin conexión');
  }
}

class ExpiringOfferApi extends CourierApi {
  @override
  Future<dynamic> call(String path,
      {String method = 'GET', Object? body, bool retry = true}) async {
    if (path == '/couriers/offers') {
      return [
        {
          'id': 'offer-1',
          'orderId': 'order-2',
          'state': 'pending',
          'expiresAt':
              DateTime.now().add(const Duration(seconds: 2)).toIso8601String()
        }
      ];
    }
    return super.call(path, method: method, body: body, retry: retry);
  }
}

class DelayedLocationApi extends CourierApi {
  int locations = 0;
  final pending = Completer<dynamic>();
  @override
  Future<dynamic> call(String path,
      {String method = 'GET', Object? body, bool retry = true}) async {
    if (path == '/deliveries/order-1/location') {
      locations++;
      if (locations > 1) return pending.future;
      return null;
    }
    return super.call(path, method: method, body: body, retry: retry);
  }
}

class RevokedCourierApi extends CourierApi {
  bool revoked = false;
  @override
  Future<dynamic> call(String path,
      {String method = 'GET', Object? body, bool retry = true}) async {
    if (revoked) throw DeliveryApiException(401, 'Ingresa de nuevo con OTP.');
    return super.call(path, method: method, body: body, retry: retry);
  }
}
