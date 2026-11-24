import 'dart:convert';
import 'package:flutter_secure_storage/flutter_secure_storage.dart';
import 'package:http/http.dart' as http;
import 'package:http_parser/http_parser.dart';

class DeliveryApiException implements Exception {
  DeliveryApiException(this.statusCode, this.message);
  final int statusCode;
  final String message;
  @override
  String toString() => 'Exception: $message';
}

class DeliveryApi {
  DeliveryApi({required this.baseUrl, http.Client? client})
      : _client = client ?? http.Client();
  final String baseUrl;
  final http.Client _client;
  final FlutterSecureStorage _storage = const FlutterSecureStorage();
  String? _access;
  Future<void>? _refreshing;
  Future<dynamic> call(String path,
      {String method = 'GET', Object? body, bool retry = true}) async {
    final uri = Uri.parse('$baseUrl/api/v1$path');
    if (uri.scheme != 'https' && !const bool.fromEnvironment('ALLOW_HTTP')) {
      throw Exception('La conexión requiere HTTPS.');
    }
    final request = http.Request(method, uri);
    request.headers['Content-Type'] = 'application/json';
    if (_access != null) request.headers['Authorization'] = 'Bearer $_access';
    if (body != null) request.body = jsonEncode(body);
    final response = await http.Response.fromStream(
        await _client.send(request).timeout(const Duration(seconds: 30)));
    if (response.statusCode == 401 &&
        retry &&
        path != '/identity/sessions/refresh' &&
        path != '/identity/login' &&
        !path.startsWith('/identity/otp/')) {
      await restore();
      return call(path, method: method, body: body, retry: false);
    }
    dynamic value;
    if (response.body.isNotEmpty) {
      try {
        value = jsonDecode(response.body);
      } catch (_) {
        value = null;
      }
    }
    if (response.statusCode >= 400) {
      throw DeliveryApiException(
          response.statusCode,
          value is Map
              ? value['detail'] ??
                  value['title'] ??
                  'No se pudo completar la operación.'
              : 'Servicio no disponible.');
    }
    return value;
  }

  Future<void> verify(String challenge, String code) async {
    final session = await call('/identity/otp/verify',
        method: 'POST',
        body: {'challengeId': challenge, 'code': code},
        retry: false);
    await _store(session);
  }

  Future<void> restore() =>
      _refreshing ??= _restore().whenComplete(() => _refreshing = null);

  Future<void> _restore() async {
    final refresh = await _storage.read(key: 'delivery.refresh');
    if (refresh == null) {
      throw DeliveryApiException(401, 'Inicia sesión para continuar.');
    }
    try {
      final session = await call('/identity/sessions/refresh',
          method: 'POST', body: {'refreshToken': refresh}, retry: false);
      await _store(session);
    } on DeliveryApiException catch (error) {
      // Only a definitive authentication rejection invalidates the saved session.
      // Network errors, rate limits and server outages must remain recoverable.
      if (error.statusCode == 401) {
        _access = null;
        await _storage.delete(key: 'delivery.refresh');
        throw DeliveryApiException(401,
            'La sesión ya no es válida. Ingresa de nuevo con el código de tu correo.');
      }
      rethrow;
    }
  }

  Future<void> _store(dynamic session) async {
    _access = session['accessToken'];
    await _storage.write(
        key: 'delivery.refresh', value: session['refreshToken']);
  }

  Future<void> logout() async {
    try {
      await call('/identity/logout', method: 'POST', body: {});
    } finally {
      _access = null;
      await _storage.delete(key: 'delivery.refresh');
    }
  }

  Future<String> upload(String name, List<int> bytes, String type) async {
    final uri = Uri.parse('$baseUrl/api/v1/documents');
    if (uri.scheme != 'https' && !const bool.fromEnvironment('ALLOW_HTTP')) {
      throw Exception('La conexión requiere HTTPS.');
    }
    if (bytes.length > 5242880) {
      throw Exception('La fotografía debe pesar menos de 5 MB.');
    }
    final request = http.MultipartRequest('POST', uri);
    request.headers['Authorization'] = 'Bearer $_access';
    request.files.add(http.MultipartFile.fromBytes('file', bytes,
        filename: name, contentType: MediaType.parse(type)));
    final response = await http.Response.fromStream(
        await _client.send(request).timeout(const Duration(seconds: 30)));
    if (response.statusCode >= 400) {
      throw Exception('No se pudo adjuntar el documento.');
    }
    return jsonDecode(response.body)['id'] as String;
  }

  void dispose() => _client.close();
}
