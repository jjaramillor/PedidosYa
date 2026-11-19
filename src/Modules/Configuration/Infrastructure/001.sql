CREATE EXTENSION IF NOT EXISTS postgis;
CREATE TABLE IF NOT EXISTS platform.documents(id uuid PRIMARY KEY,owner_id uuid NOT NULL,name text NOT NULL,content_type text NOT NULL,length bigint NOT NULL,allowed_users uuid[] NOT NULL);
CREATE SCHEMA IF NOT EXISTS configuration;
CREATE TABLE IF NOT EXISTS platform.audit (
 id uuid PRIMARY KEY,module text NOT NULL,actor_id uuid NOT NULL,subject_id uuid NOT NULL,action text NOT NULL,
 reason text NOT NULL,correlation_id text NOT NULL,occurred_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE configuration.zones (
 id uuid PRIMARY KEY,name text NOT NULL,polygon geometry(Polygon,4326) NOT NULL,active boolean NOT NULL,
 delivery_base bigint NOT NULL CHECK(delivery_base>=0),per_kilometer bigint NOT NULL CHECK(per_kilometer>=0),
 service_fee bigint NOT NULL CHECK(service_fee>=0),commission_bps integer NOT NULL CHECK(commission_bps BETWEEN 0 AND 10000),
 courier_base bigint NOT NULL CHECK(courier_base>=0),acceptance_seconds integer NOT NULL CHECK(acceptance_seconds BETWEEN 30 AND 3600),
 cash_enabled boolean NOT NULL, CHECK(ST_IsValid(polygon))
);
CREATE INDEX ix_zones_polygon ON configuration.zones USING gist(polygon);
CREATE TABLE configuration.parameters(name text PRIMARY KEY,value jsonb NOT NULL,updated_at timestamptz NOT NULL DEFAULT now());
INSERT INTO configuration.parameters(name,value) VALUES ('cash.enabled','false'),('orders.enabled','true');
