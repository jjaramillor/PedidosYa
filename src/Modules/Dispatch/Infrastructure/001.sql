CREATE SCHEMA dispatch;
CREATE TABLE dispatch.couriers(id uuid PRIMARY KEY,vehicle text NOT NULL CHECK(vehicle IN('bicycle','motorbike','car')),documents jsonb NOT NULL,status text NOT NULL CHECK(status IN('pending','approved','rejected','suspended')),available boolean NOT NULL DEFAULT false,zone_id uuid,latitude double precision,longitude double precision,located_at timestamptz,decision_reason text);
CREATE TABLE dispatch.offers(id uuid PRIMARY KEY,order_id uuid NOT NULL,courier_id uuid NOT NULL REFERENCES dispatch.couriers(id),state text NOT NULL CHECK(state IN('pending','accepted','rejected','expired','cancelled','completed')),expires_at timestamptz NOT NULL,created_at timestamptz NOT NULL DEFAULT now());
CREATE UNIQUE INDEX ix_dispatch_active_order ON dispatch.offers(order_id) WHERE state='accepted';
CREATE UNIQUE INDEX ix_dispatch_active_courier ON dispatch.offers(courier_id) WHERE state='accepted';

