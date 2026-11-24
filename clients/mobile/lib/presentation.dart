import 'package:flutter/material.dart';

const ink = Color(0xff203c32);
const canvas = Color(0xfff7f8f4);
ThemeData deliveryTheme() => ThemeData(
      useMaterial3: true,
      scaffoldBackgroundColor: canvas,
      colorScheme: ColorScheme.fromSeed(
          seedColor: ink,
          primary: ink,
          secondary: const Color(0xffb94929),
          surface: Colors.white),
      appBarTheme: const AppBarTheme(
          backgroundColor: canvas,
          foregroundColor: ink,
          elevation: 0,
          scrolledUnderElevation: 0),
      inputDecorationTheme: InputDecorationTheme(
          filled: true,
          fillColor: Colors.white,
          contentPadding: const EdgeInsets.all(16),
          border: OutlineInputBorder(
              borderRadius: BorderRadius.circular(14),
              borderSide: const BorderSide(color: Color(0xffdce3dc))),
          enabledBorder: OutlineInputBorder(
              borderRadius: BorderRadius.circular(14),
              borderSide: const BorderSide(color: Color(0xffdce3dc)))),
      filledButtonTheme: FilledButtonThemeData(
          style: FilledButton.styleFrom(
              minimumSize: const Size(48, 50),
              padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 14),
              shape: RoundedRectangleBorder(
                  borderRadius: BorderRadius.circular(14)))),
      outlinedButtonTheme: OutlinedButtonThemeData(
          style: OutlinedButton.styleFrom(
              minimumSize: const Size(48, 48),
              padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
              shape: RoundedRectangleBorder(
                  borderRadius: BorderRadius.circular(14)))),
      cardTheme: CardTheme(
          elevation: 0,
          margin: const EdgeInsets.symmetric(vertical: 8),
          color: Colors.white,
          shape: RoundedRectangleBorder(
              borderRadius: BorderRadius.circular(18),
              side: const BorderSide(color: Color(0xffe0e5df)))),
      navigationBarTheme: const NavigationBarThemeData(
          backgroundColor: Colors.white, indicatorColor: Color(0xffe3eadc)),
    );

class IntroPanel extends StatelessWidget {
  const IntroPanel(
      {super.key,
      required this.eyebrow,
      required this.title,
      required this.description,
      this.icon = Icons.restaurant_rounded});
  final String eyebrow, title, description;
  final IconData icon;
  @override
  Widget build(BuildContext context) => Container(
        margin: const EdgeInsets.only(bottom: 24),
        padding: const EdgeInsets.all(24),
        decoration: BoxDecoration(
            color: const Color(0xffeeeee3),
            borderRadius: BorderRadius.circular(24)),
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Icon(icon, color: const Color(0xffb94929), size: 32),
          const SizedBox(height: 16),
          Text(eyebrow,
              style: const TextStyle(
                  fontSize: 11,
                  letterSpacing: 1.5,
                  fontWeight: FontWeight.w700,
                  color: ink)),
          const SizedBox(height: 12),
          Text(title,
              style: const TextStyle(
                  fontSize: 32,
                  height: 1.1,
                  fontWeight: FontWeight.w800,
                  letterSpacing: -1,
                  color: ink)),
          const SizedBox(height: 12),
          Text(description,
              style: const TextStyle(height: 1.5, color: Color(0xff526258))),
        ]),
      );
}

class SectionTitle extends StatelessWidget {
  const SectionTitle(this.title, {super.key, this.subtitle});
  final String title;
  final String? subtitle;
  @override
  Widget build(BuildContext context) => Padding(
      padding: const EdgeInsets.only(top: 24, bottom: 12),
      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Text(title,
            style: const TextStyle(
                fontSize: 24, fontWeight: FontWeight.w700, color: ink)),
        if (subtitle != null) ...[
          const SizedBox(height: 6),
          Text(subtitle!,
              style: const TextStyle(color: Color(0xff62736b), height: 1.4))
        ],
      ]));
}

String deliveryStateLabel(String state) =>
    const {
      'assigned': 'Por recoger',
      'arrived': 'En el restaurante',
      'picked_up': 'Retiro confirmado',
      'in_transit': 'En camino',
      'incident': 'Con incidencia',
      'delivered': 'Entregado',
      'returned': 'Devuelto',
      'confirmed': 'Confirmado',
      'accepted': 'Aceptado',
      'preparing': 'En cocina',
      'ready': 'Listo para retiro',
      'closed': 'Completado',
      'cancelled': 'Cancelado',
      'pending_payment': 'Pago pendiente',
    }[state] ??
    state;
