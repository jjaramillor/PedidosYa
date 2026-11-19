import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:delivery_mobile/main.dart';

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
}
