import 'dart:async';
import 'package:flutter/material.dart';
import 'package:geolocator/geolocator.dart';
import 'package:url_launcher/url_launcher.dart';
import 'package:uuid/uuid.dart';
import 'api.dart';

class CustomerArea extends StatefulWidget {
  const CustomerArea({super.key, required this.api});
  final DeliveryApi api;
  @override
  State<CustomerArea> createState() => _CustomerAreaState();
}

class _CustomerAreaState extends State<CustomerArea>
    with WidgetsBindingObserver {
  bool busy = false;
  String? error;
  String? addressId;
  double latitude = -12.0464, longitude = -77.0428;
  List<dynamic> addresses = [],
      branches = [],
      products = [],
      orders = [],
      cart = [];
  dynamic branch, quote, order, delivery;
  final search = TextEditingController(),
      coupon = TextEditingController(),
      tip = TextEditingController(text: '0');
  String method = 'cash';
  dynamic settings;
  Timer? polling;
  bool refreshing = false;
  String? checkoutId;
  String money(dynamic value) => 'S/ ${(value / 100).toStringAsFixed(2)}';
  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    run(reload);
  }

  @override
  void dispose() {
    polling?.cancel();
    WidgetsBinding.instance.removeObserver(this);
    search.dispose();
    coupon.dispose();
    tip.dispose();
    super.dispose();
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    if (state != AppLifecycleState.resumed) {
      polling?.cancel();
      polling = null;
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
        setState(
            () => error = exception.toString().replaceFirst('Exception: ', ''));
      }
    } finally {
      if (mounted) setState(() => busy = false);
    }
  }

  Future<void> reload() async {
    addresses = await widget.api.call('/addresses');
    addressId ??= addresses.isEmpty ? null : addresses.first['id'];
    settings = await widget.api.call('/payments/settings');
    orders = await widget.api.call('/orders');
    await browse();
  }

  Future<void> browse() async {
    final address = addresses.where((a) => a['id'] == addressId).firstOrNull;
    if (address != null) {
      latitude = address['location']['latitude'];
      longitude = address['location']['longitude'];
    }
    branches = await widget.api.call(
        '/catalog/search?latitude=$latitude&longitude=$longitude&onlyOpen=true&text=${Uri.encodeQueryComponent(search.text)}');
  }

  Future<Position> locate() async {
    var permission = await Geolocator.checkPermission();
    if (permission == LocationPermission.denied) {
      permission = await Geolocator.requestPermission();
    }
    if (permission == LocationPermission.denied ||
        permission == LocationPermission.deniedForever) {
      throw Exception('Permite la ubicación o usa una dirección guardada.');
    }
    return Geolocator.getCurrentPosition(
        locationSettings: const LocationSettings(
            accuracy: LocationAccuracy.high, timeLimit: Duration(seconds: 15)));
  }

  Future<Map<String, String>?> form(String title, List<String> labels) async {
    final controllers = {
      for (final label in labels) label: TextEditingController()
    };
    final result = await showDialog<Map<String, String>>(
        context: context,
        builder: (context) => AlertDialog(
                title: Text(title),
                content: SingleChildScrollView(
                    child: Column(mainAxisSize: MainAxisSize.min, children: [
                  for (final label in labels)
                    TextField(
                        controller: controllers[label],
                        decoration: InputDecoration(labelText: label))
                ])),
                actions: [
                  TextButton(
                      onPressed: () => Navigator.pop(context),
                      child: const Text('Cancelar')),
                  FilledButton(
                      onPressed: () => Navigator.pop(context, {
                            for (final entry in controllers.entries)
                              entry.key: entry.value.text.trim()
                          }),
                      child: const Text('Guardar'))
                ]));
    for (final controller in controllers.values) {
      controller.dispose();
    }
    return result;
  }

  Future<void> address() async {
    final point = await locate();
    if (!mounted) return;
    final input =
        await form('Nueva dirección', ['Etiqueta', 'Dirección', 'Referencia']);
    if (input == null) return;
    final location = {'latitude': point.latitude, 'longitude': point.longitude};
    await widget.api.call('/coverage', method: 'POST', body: location);
    final result = await widget.api.call('/addresses', method: 'POST', body: {
      'label': input['Etiqueta'],
      'street': input['Dirección'],
      'reference': input['Referencia'],
      'location': location
    });
    addressId = result['id'];
    await reload();
  }

  Future<void> openBranch(dynamic selected) async {
    final result = await widget.api.call('/catalog/branches/${selected['id']}');
    branch = result['branch'];
    products = result['products'];
  }

  Future<void> add(dynamic product) async {
    if (cart.isNotEmpty && cart.first['branchId'] != branch['id']) {
      throw Exception('Vacía el carrito antes de cambiar de restaurante.');
    }
    final selected = <String>{};
    if (product['modifiers'].isNotEmpty) {
      if (!mounted) return;
      final accepted = await showDialog<bool>(
          context: context,
          builder: (context) => StatefulBuilder(
              builder: (context, update) => AlertDialog(
                      title: Text(product['name']),
                      content: SingleChildScrollView(
                          child:
                              Column(mainAxisSize: MainAxisSize.min, children: [
                        for (final option in product['modifiers'])
                          CheckboxListTile(
                              title: Text(
                                  '${option['name']} (+${money(option['price'])})'),
                              subtitle: Text(option['group']),
                              value: selected.contains(option['id']),
                              onChanged: (value) => update(() {
                                    if (value == true) {
                                      selected.add(option['id']);
                                    } else {
                                      selected.remove(option['id']);
                                    }
                                  }))
                      ])),
                      actions: [
                        TextButton(
                            onPressed: () => Navigator.pop(context, false),
                            child: const Text('Cancelar')),
                        FilledButton(
                            onPressed: () => Navigator.pop(context, true),
                            child: const Text('Añadir'))
                      ])));
      if (accepted != true) return;
    }
    cart.add({
      'branchId': branch['id'],
      'productId': product['id'],
      'name': product['name'],
      'quantity': 1,
      'modifierIds': selected.toList()
    });
    quote = null;
    checkoutId = null;
    await saveCart();
  }

  List<Map<String, dynamic>> lines() => cart
      .map((item) => {
            'productId': item['productId'],
            'quantity': item['quantity'],
            'modifierIds': item['modifierIds']
          })
      .toList();
  Future<void> saveCart() async {
    if (cart.isNotEmpty) {
      await widget.api.call('/cart',
          method: 'PUT',
          body: {'branchId': cart.first['branchId'], 'lines': lines()});
    }
  }

  Future<void> calculate() async {
    if (addressId == null || cart.isEmpty) {
      throw Exception('Selecciona una dirección y añade productos.');
    }
    checkoutId = null;
    quote = await widget.api.call('/quotes', method: 'POST', body: {
      'addressId': addressId,
      'branchId': cart.first['branchId'],
      'lines': lines(),
      'coupon': coupon.text.isEmpty ? null : coupon.text,
      'tip': ((double.tryParse(tip.text) ?? 0) * 100).round()
    });
  }

  Future<void> checkout() async {
    if (quote == null) throw Exception('Calcula el total.');
    checkoutId ??= const Uuid().v4();
    final placed = await widget.api.call('/orders', method: 'POST', body: {
      'operationId': checkoutId,
      'quoteId': quote['id'],
      'method': method,
      'cardToken': method == 'card'
          ? '${'{'}"token":"sandbox-approved","paymentMethodId":"visa","issuerId":null}'
          : null
    });
    await widget.api.call('/cart',
        method: 'PUT', body: {'branchId': placed['branchId'], 'lines': []});
    cart.clear();
    quote = null;
    orders = await widget.api.call('/orders');
    await openOrder(placed['id']);
  }

  Future<void> webCheckout() async {
    await saveCart();
    final uri = Uri.parse(const String.fromEnvironment('WEB_URL',
        defaultValue: 'https://localhost:4200'));
    if (uri.scheme != 'https' && !const bool.fromEnvironment('ALLOW_HTTP')) {
      throw Exception('El checkout requiere HTTPS.');
    }
    if (!await launchUrl(uri, mode: LaunchMode.externalApplication)) {
      throw Exception('No se pudo abrir el checkout seguro.');
    }
  }

  Future<void> openOrder(String id) async {
    order = await widget.api.call('/orders/$id');
    try {
      delivery = await widget.api.call('/deliveries/$id');
    } catch (_) {
      delivery = null;
    }
    polling?.cancel();
    if (!['closed', 'cancelled', 'returned'].contains(order['state'])) {
      polling = Timer.periodic(const Duration(seconds: 10), (_) async {
        if (busy || refreshing) return;
        refreshing = true;
        try {
          final fresh = await widget.api.call('/orders/$id');
          dynamic fulfillment;
          try {
            fulfillment = await widget.api.call('/deliveries/$id');
          } catch (_) {}
          if (mounted) {
            setState(() {
              order = fresh;
              delivery = fulfillment;
            });
          }
          if (['closed', 'cancelled', 'returned'].contains(fresh['state'])) {
            polling?.cancel();
            polling = null;
          }
        } catch (exception) {
          if (mounted) setState(() => error = exception.toString());
        } finally {
          refreshing = false;
        }
      });
    }
  }

  Future<void> rating() async {
    final input = await form('Valorar entrega',
        ['Restaurante (1–5)', 'Repartidor (1–5)', 'Comentario']);
    if (input == null) return;
    await widget.api
        .call('/orders/${order['id']}/rating', method: 'POST', body: {
      'merchantStars': int.tryParse(input['Restaurante (1–5)'] ?? '') ?? 0,
      'courierStars': int.tryParse(input['Repartidor (1–5)'] ?? '') ?? 0,
      'comment': input['Comentario']
    });
  }

  Future<void> support() async {
    final input = await form('Solicitar ayuda', ['Asunto', 'Descripción']);
    if (input == null) return;
    await widget.api.call('/support/cases', method: 'POST', body: {
      'orderId': order['id'],
      'subject': input['Asunto'],
      'description': input['Descripción'],
      'evidenceIds': []
    });
  }

  @override
  Widget build(BuildContext context) => Scaffold(
      appBar: AppBar(title: const Text('Pedir comida')),
      body: ListView(padding: const EdgeInsets.all(24), children: [
        if (busy) const LinearProgressIndicator(),
        if (error != null)
          Text(error!,
              style: TextStyle(color: Theme.of(context).colorScheme.error)),
        DropdownButton<String>(
            isExpanded: true,
            value:
                addresses.any((a) => a['id'] == addressId) ? addressId : null,
            hint: const Text('Dirección de entrega'),
            items: [
              for (final item in addresses)
                DropdownMenuItem(
                    value: item['id'] as String,
                    child: Text('${item['label']} · ${item['street']}'))
            ],
            onChanged: busy
                ? null
                : (value) {
                    setState(() {
                      addressId = value;
                      quote = null;
                    });
                    run(browse);
                  }),
        OutlinedButton(
            onPressed: busy ? null : () => run(address),
            child: const Text('Añadir dirección con mi ubicación')),
        TextField(
            controller: search,
            decoration:
                const InputDecoration(labelText: 'Restaurante o producto')),
        FilledButton(
            onPressed: busy ? null : () => run(browse),
            child: const Text('Buscar restaurantes')),
        for (final item in branches)
          Card(
              child: ListTile(
                  title: Text(item['branch']['name']),
                  subtitle:
                      Text('${item['etaMinutes']} min · ★ ${item['rating']}'),
                  onTap: busy
                      ? null
                      : () => run(() => openBranch(item['branch'])))),
        if (branch != null) ...[
          Text(branch['name'],
              style:
                  const TextStyle(fontSize: 24, fontWeight: FontWeight.bold)),
          for (final product in products)
            Card(
                child: ListTile(
                    title: Text(product['name']),
                    subtitle: Text(
                        '${product['description']} · ${money(product['price'])}'),
                    trailing: IconButton(
                        icon: const Icon(Icons.add),
                        onPressed: busy ||
                                !product['available'] ||
                                product['stock'] == 0
                            ? null
                            : () => run(() => add(product)))))
        ],
        if (cart.isNotEmpty) ...[
          const Divider(),
          const Text('Tu carrito',
              style: TextStyle(fontSize: 24, fontWeight: FontWeight.bold)),
          for (var i = 0; i < cart.length; i++)
            ListTile(
                title: Text('${cart[i]['quantity']} × ${cart[i]['name']}'),
                trailing: IconButton(
                    icon: const Icon(Icons.delete_outline),
                    onPressed: busy
                        ? null
                        : () => run(() async {
                              final branchId = cart[i]['branchId'];
                              cart.removeAt(i);
                              quote = null;
                              await widget.api.call('/cart',
                                  method: 'PUT',
                                  body: {
                                    'branchId': branchId,
                                    'lines': lines()
                                  });
                            }))),
          TextField(
              controller: coupon,
              decoration: const InputDecoration(labelText: 'Cupón'),
              onChanged: (_) => setState(() => quote = null)),
          TextField(
              controller: tip,
              keyboardType: TextInputType.number,
              decoration: const InputDecoration(labelText: 'Propina (S/)'),
              onChanged: (_) => setState(() => quote = null)),
          OutlinedButton(
              onPressed: busy ? null : () => run(calculate),
              child: const Text('Calcular total')),
          if (quote != null) ...[
            Text('Productos: ${money(quote['subtotal'])}'),
            Text(
                'Entrega: ${money(quote['delivery'])} · Servicio: ${money(quote['service'])}'),
            Text(
                'Descuento: ${money(quote['discount'])} · Propina: ${money(quote['tip'])}'),
            Text('Total: ${money(quote['total'])}',
                style:
                    const TextStyle(fontSize: 22, fontWeight: FontWeight.bold)),
            DropdownButton<String>(
                value: method,
                items: [
                  const DropdownMenuItem(
                      value: 'cash',
                      child: Text('Efectivo (si está habilitado)')),
                  if (settings?['provider'] == 'sandbox')
                    const DropdownMenuItem(
                        value: 'card',
                        child: Text('Tarjeta de prueba, sin cobro real'))
                ],
                onChanged:
                    busy ? null : (value) => setState(() => method = value!)),
            FilledButton(
                onPressed: busy ? null : () => run(checkout),
                child: const Text('Confirmar pedido')),
            if (settings?['provider'] == 'mercadopago')
              OutlinedButton(
                  onPressed: busy ? null : () => run(webCheckout),
                  child: const Text('Pagar con tarjeta en el checkout seguro'))
          ]
        ],
        const Divider(),
        const Text('Mis pedidos',
            style: TextStyle(fontSize: 24, fontWeight: FontWeight.bold)),
        OutlinedButton(
            onPressed: busy ? null : () => run(reload),
            child: const Text('Actualizar')),
        for (final item in orders)
          ListTile(
              title: Text(item['quote']['branch']['name']),
              subtitle:
                  Text('${item['state']} · ${money(item['quote']['total'])}'),
              onTap: busy ? null : () => run(() => openOrder(item['id']))),
        if (order != null) ...[
          Text('Estado: ${order['state']}'),
          Text('Destino: ${order['quote']['address']['street']}'),
          if (delivery?['deliveryPin'] != null)
            Text('Código de entrega: ${delivery['deliveryPin']}',
                style:
                    const TextStyle(fontSize: 24, fontWeight: FontWeight.bold)),
          if (delivery?['etaMinutes'] != null)
            Text('Llegada estimada: ${delivery['etaMinutes']} min'),
          if (['closed', 'delivered'].contains(order['state']))
            OutlinedButton(
                onPressed: busy ? null : () => run(rating),
                child: const Text('Valorar restaurante y repartidor')),
          OutlinedButton(
              onPressed: busy ? null : () => run(support),
              child: const Text('Solicitar ayuda'))
        ]
      ]));
}
