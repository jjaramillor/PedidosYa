import 'package:flutter/material.dart';

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

class OfferDetails extends StatelessWidget {
  const OfferDetails({super.key, required this.offer});
  final Map<dynamic, dynamic> offer;
  @override
  Widget build(BuildContext context) {
    final preview = offer['preview'];
    final expiry =
        DateTime.tryParse(offer['expiresAt']?.toString() ?? '')?.toLocal();
    final time = expiry == null
        ? 'Consulta la vigencia antes de aceptar'
        : 'Disponible hasta ${TimeOfDay.fromDateTime(expiry).format(context)}';
    if (preview is! Map) {
      return Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Text('Pedido ${offer['orderId']}'),
        const Text(
            'Detalle de oferta no disponible. Actualiza antes de aceptar.'),
        Text(time)
      ]);
    }
    String money(dynamic amount) =>
        '${preview['currency']} ${((amount as num) / 100).toStringAsFixed(2)}';
    return Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
      Text(preview['restaurantName'] as String,
          style: const TextStyle(fontSize: 20, fontWeight: FontWeight.w700)),
      const SizedBox(height: 8),
      Text(preview['pickupKilometers'] == null
          ? 'Distancia al restaurante no disponible'
          : '${(preview['pickupKilometers'] as num).toStringAsFixed(1)} km hasta el restaurante'),
      Text(
          '${(preview['deliveryKilometers'] as num).toStringAsFixed(1)} km de entrega'),
      const Text('Distancias aproximadas en línea recta.',
          style: TextStyle(fontSize: 12)),
      const SizedBox(height: 12),
      Text('Ganancia estimada: ${money(preview['estimatedEarnings'])}',
          style: const TextStyle(fontWeight: FontWeight.w700)),
      Text(preview['paymentMethod'] == 'cash'
          ? 'Efectivo por cobrar: ${money(preview['cashToCollect'])}'
          : 'Pago con tarjeta · no cobres efectivo'),
      const SizedBox(height: 8),
      Text(time),
    ]);
  }
}
