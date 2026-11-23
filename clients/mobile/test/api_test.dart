import 'dart:convert';
import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:http/http.dart' as http;
import 'package:http/testing.dart';
import 'package:delivery_mobile/api.dart';

void main() {
  TestWidgetsFlutterBinding.ensureInitialized();
  late Map<String, String> storage;
  setUp(() {
    storage = {'delivery.refresh': 'saved-refresh'};
    TestDefaultBinaryMessengerBinding.instance.defaultBinaryMessenger
        .setMockMethodCallHandler(
            const MethodChannel('plugins.it_nomads.com/flutter_secure_storage'),
            (call) async {
      final args = Map<String, dynamic>.from(call.arguments as Map);
      final key = args['key'] as String;
      switch (call.method) {
        case 'read':
          return storage[key];
        case 'write':
          storage[key] = args['value'] as String;
          return null;
        case 'delete':
          storage.remove(key);
          return null;
      }
      throw StateError('Unexpected storage operation: ${call.method}');
    });
  });

  test('refresh survives a network failure and succeeds on manual retry',
      () async {
    var online = false;
    var requests = 0;
    final api = DeliveryApi(
        baseUrl: 'https://example.test',
        client: MockClient((r) async {
          requests++;
          if (!online) throw http.ClientException('Offline');
          expect(jsonDecode(r.body)['refreshToken'], 'saved-refresh');
          return http.Response(
              jsonEncode({'accessToken': 'access', 'refreshToken': 'rotated'}),
              200);
        }));
    addTearDown(api.dispose);
    await expectLater(api.restore(), throwsA(isA<http.ClientException>()));
    expect(storage['delivery.refresh'], 'saved-refresh');
    expect(requests, 1);
    online = true;
    await api.restore();
    expect(storage['delivery.refresh'], 'rotated');
    expect(requests, 2);
  });

  for (final status in [429, 500, 503]) {
    test('refresh preserves credentials after HTTP $status', () async {
      final api = DeliveryApi(
          baseUrl: 'https://example.test',
          client: MockClient((r) async => http.Response('{}', status)));
      addTearDown(api.dispose);
      await expectLater(api.restore(), throwsA(isA<DeliveryApiException>()));
      expect(storage['delivery.refresh'], 'saved-refresh');
    });
  }

  test('a definitively revoked refresh token is removed', () async {
    final api = DeliveryApi(
        baseUrl: 'https://example.test',
        client: MockClient((r) async => http.Response('{}', 401)));
    addTearDown(api.dispose);
    await expectLater(api.restore(), throwsA(isA<DeliveryApiException>()));
    expect(storage.containsKey('delivery.refresh'), isFalse);
  });

  test('an incorrect delivery PIN does not refresh or resend the action',
      () async {
    final paths = <String>[];
    final api = DeliveryApi(
        baseUrl: 'https://example.test',
        client: MockClient((r) async {
          paths.add(r.url.path);
          return http.Response(
              '{"code":"invalid_delivery_pin","title":"PIN incorrecto"}', 422);
        }));
    addTearDown(api.dispose);
    await expectLater(
        api.call('/deliveries/order/actions',
            method: 'POST', body: {'pin': '000000'}),
        throwsA(isA<DeliveryApiException>()
            .having((e) => e.statusCode, 'status', 422)));
    expect(paths, ['/api/v1/deliveries/order/actions']);
    expect(storage['delivery.refresh'], 'saved-refresh');
  });

  test('a lost mutation response is not automatically replayed', () async {
    var requests = 0;
    final api = DeliveryApi(
        baseUrl: 'https://example.test',
        client: MockClient((r) async {
          requests++;
          throw http.ClientException('Connection closed after sending');
        }));
    addTearDown(api.dispose);
    await expectLater(
        api.call('/deliveries/order/actions', method: 'POST', body: {}),
        throwsA(isA<http.ClientException>()));
    expect(requests, 1);
  });
}
