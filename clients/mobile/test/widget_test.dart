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
    expect(find.text('Solicitud de alta'), findsOneWidget);
    expect(find.text('Llegué al restaurante'), findsNothing);
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
}

class CourierApi extends DeliveryApi {
  CourierApi() : super(baseUrl: 'https://example.test');
  String state = 'assigned';
  bool loseActionResponse = false;
  int actions = 0;
  @override
  Future<void> restore() async {}
  @override
  Future<dynamic> call(String path,
      {String method = 'GET', Object? body, bool retry = true}) async {
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
