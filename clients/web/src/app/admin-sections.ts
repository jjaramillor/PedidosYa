export const adminSections = [
  {
    id: 'orders',
    label: 'Pedidos',
    description: 'Monitoreo e intervención de entregas.',
    permissions: ['orders.operate'],
  },
  {
    id: 'participants',
    label: 'Participantes',
    description: 'Afiliaciones, cuentas y permisos.',
    permissions: ['participants.manage', 'identity.users.manage', 'identity.roles.manage'],
  },
  {
    id: 'support',
    label: 'Soporte',
    description: 'Casos, evidencias y tiempos de atención.',
    permissions: ['support.manage'],
  },
  {
    id: 'finance',
    label: 'Finanzas',
    description: 'Conciliación, saldos y liquidaciones.',
    permissions: ['finance.manage'],
  },
  {
    id: 'configuration',
    label: 'Configuración',
    description: 'Cobertura, tarifas, parámetros y promociones.',
    permissions: ['configuration.manage', 'promotions.manage'],
  },
  {
    id: 'audit',
    label: 'Auditoría',
    description: 'Historial de las intervenciones de la plataforma.',
    permissions: ['platform.audit.read'],
  },
];
export function visibleAdminSections(permissions: readonly string[]) {
  return adminSections.filter((section) =>
    section.permissions.some((permission) => permissions.includes(permission)),
  );
}

export interface AdminRole {
  id: string;
  name: string;
  permissions: string[];
  systemRole: boolean;
}
export function canDeleteCustomRole(roles: readonly AdminRole[], id: string) {
  return roles.some((role) => role.id === id && !role.systemRole);
}
