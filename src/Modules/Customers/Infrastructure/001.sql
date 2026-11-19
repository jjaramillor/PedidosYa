CREATE SCHEMA customers;
CREATE TABLE customers.addresses(id uuid PRIMARY KEY,user_id uuid NOT NULL,label text NOT NULL,street text NOT NULL,reference text NOT NULL,latitude double precision NOT NULL CHECK(latitude BETWEEN -90 AND 90),longitude double precision NOT NULL CHECK(longitude BETWEEN -180 AND 180),updated_at timestamptz NOT NULL DEFAULT now());
CREATE INDEX ix_addresses_user ON customers.addresses(user_id);
CREATE TABLE customers.ratings(order_id uuid PRIMARY KEY,user_id uuid NOT NULL,branch_id uuid NOT NULL,merchant_stars integer NOT NULL CHECK(merchant_stars BETWEEN 1 AND 5),courier_stars integer NOT NULL CHECK(courier_stars BETWEEN 1 AND 5),comment text NOT NULL,created_at timestamptz NOT NULL DEFAULT now());

