CREATE SCHEMA deliveries;
CREATE TABLE deliveries.deliveries(order_id uuid PRIMARY KEY,courier_id uuid NOT NULL,state text NOT NULL CHECK(state IN('assigned','arrived','picked_up','in_transit','delivered','incident','returned','cancelled')),pickup_pin text NOT NULL,drop_pin text NOT NULL,pickup_attempts integer NOT NULL DEFAULT 0,drop_attempts integer NOT NULL DEFAULT 0,evidence_id uuid,delivered_at timestamptz,updated_at timestamptz NOT NULL DEFAULT now());
CREATE INDEX ix_delivery_courier ON deliveries.deliveries(courier_id,state);
CREATE TABLE deliveries.locations(id uuid PRIMARY KEY,order_id uuid NOT NULL,courier_id uuid NOT NULL,latitude double precision NOT NULL,longitude double precision NOT NULL,recorded_at timestamptz NOT NULL DEFAULT now());
CREATE INDEX ix_delivery_locations ON deliveries.locations(order_id,recorded_at DESC);
CREATE TABLE deliveries.operations(id uuid PRIMARY KEY,order_id uuid NOT NULL,state text NOT NULL);

