/// Historical and expired offers remain in the API for traceability.
/// Only pending offers with time remaining can be accepted by the courier.
List<dynamic> actionableOffers(List<dynamic> offers, DateTime now) {
  return offers.where((offer) {
    if (offer is! Map || offer['state'] != 'pending') return false;
    final rawExpiry = offer['expiresAt'];
    final expiry = rawExpiry is String ? DateTime.tryParse(rawExpiry) : null;
    return expiry != null && expiry.isAfter(now);
  }).toList(growable: false);
}
