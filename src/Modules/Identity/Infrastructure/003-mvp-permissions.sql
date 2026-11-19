INSERT INTO identity.permissions(id) VALUES ('configuration.manage'),('participants.manage'),('orders.operate'),('support.manage'),('finance.manage'),('promotions.manage') ON CONFLICT DO NOTHING;
INSERT INTO identity.permissions(id) VALUES ('payments.refund') ON CONFLICT DO NOTHING;
INSERT INTO identity.permissions(id) VALUES ('platform.audit.read') ON CONFLICT DO NOTHING;
INSERT INTO identity.role_permissions(role_id,permission_id) SELECT 'admin',id FROM identity.permissions ON CONFLICT DO NOTHING;

