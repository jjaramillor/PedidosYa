import 'package:delivery_mobile/offers.dart';
import 'package:flutter_test/flutter_test.dart';

void main() {
  final now = DateTime.utc(2026, 11, 4, 23);
  final future = now.add(const Duration(minutes: 1)).toIso8601String();

  test('completed and rejected deliveries never appear as new offers', () {
    final result = actionableOffers([
      {'id': 'done', 'state': 'completed', 'expiresAt': future},
      {'id': 'rejected', 'state': 'rejected', 'expiresAt': future},
      {'id': 'active', 'state': 'pending', 'expiresAt': future},
    ], now);
    expect(result.map((offer) => offer['id']), ['active']);
  });

  test('offers disappear at their expiration instant and afterward', () {
    expect(
        actionableOffers([
          {'state': 'pending', 'expiresAt': now.toIso8601String()},
          {
            'state': 'pending',
            'expiresAt':
                now.subtract(const Duration(seconds: 1)).toIso8601String()
          },
        ], now),
        isEmpty);
  });

  test('offers without a valid expiration cannot be accepted', () {
    expect(
        actionableOffers([
          {'state': 'pending', 'expiresAt': 'invalid'},
          {'state': 'pending'},
        ], now),
        isEmpty);
  });
}
