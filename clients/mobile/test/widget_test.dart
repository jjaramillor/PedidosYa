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
}

class CourierApi extends DeliveryApi {
  CourierApi() : super(baseUrl: 'https://example.test');
  String state = 'assigned';
  @override
  Future<void> restore() async {}
  @override
  Future<dynamic> call(String path,
      {String method = 'GET', Object? body, bool retry = true}) async {
    if (path == '/deliveries/order-1/actions' && method == 'POST') {
      state = (body as Map)['state'] as String;
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
