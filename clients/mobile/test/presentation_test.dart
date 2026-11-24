import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:delivery_mobile/api.dart';
import 'package:delivery_mobile/customer.dart';
import 'package:delivery_mobile/presentation.dart';

void main() {
  testWidgets(
      'Customer destinations separate browsing, cart and order history at narrow width',
      (tester) async {
    tester.view.physicalSize = const Size(360, 800);
    tester.view.devicePixelRatio = 1;
    addTearDown(tester.view.resetPhysicalSize);
    addTearDown(tester.view.resetDevicePixelRatio);
    await tester.pumpWidget(MaterialApp(
        theme: deliveryTheme(), home: CustomerArea(api: EmptyCustomerApi())));
    await tester.pumpAndSettle();
    expect(find.text('Hoy se come rico.'), findsOneWidget);
    expect(find.text('Tu carrito'), findsNothing);
    await tester.tap(find.text('Carrito'));
    await tester.pumpAndSettle();
    expect(find.text('Tu carrito'), findsOneWidget);
    expect(find.text('Hoy se come rico.'), findsNothing);
    expect(find.textContaining('Tu carrito está vacío'), findsOneWidget);
    await tester.tap(find.text('Pedidos'));
    await tester.pumpAndSettle();
    expect(find.text('Mis pedidos'), findsOneWidget);
    expect(find.textContaining('Aún no tienes pedidos'), findsOneWidget);
    expect(tester.takeException(), isNull);
    await tester.pumpWidget(const SizedBox());
  });
}

class EmptyCustomerApi extends DeliveryApi {
  EmptyCustomerApi() : super(baseUrl: 'https://example.test');
  @override
  Future<dynamic> call(String path,
      {String method = 'GET', Object? body, bool retry = true}) async {
    if (path == '/payments/settings') return {'provider': 'sandbox'};
    if (path == '/addresses' ||
        path == '/orders' ||
        path.startsWith('/catalog/search?')) return <dynamic>[];
    throw StateError('Unexpected request: $path');
  }
}
