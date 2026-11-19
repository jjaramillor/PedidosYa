CREATE SCHEMA merchants;
CREATE TABLE merchants.businesses(id uuid PRIMARY KEY,owner_id uuid NOT NULL,name text NOT NULL,tax_id text NOT NULL,bank_account text NOT NULL,documents jsonb NOT NULL,status text NOT NULL CHECK(status IN('pending','approved','rejected','suspended')),decision_reason text,created_at timestamptz NOT NULL DEFAULT now());
CREATE TABLE merchants.branches(id uuid PRIMARY KEY,merchant_id uuid NOT NULL REFERENCES merchants.businesses(id),name text NOT NULL,latitude double precision NOT NULL,longitude double precision NOT NULL,currency text NOT NULL CHECK(currency='PEN'),minimum_order bigint NOT NULL CHECK(minimum_order>=0),preparation_minutes integer NOT NULL CHECK(preparation_minutes BETWEEN 1 AND 180),active boolean NOT NULL,paused_until timestamptz,hours jsonb NOT NULL);
CREATE INDEX ix_branch_merchant ON merchants.branches(merchant_id);
CREATE TABLE merchants.members(branch_id uuid NOT NULL REFERENCES merchants.branches(id),user_id uuid NOT NULL,role text NOT NULL CHECK(role IN('manager','operator')),PRIMARY KEY(branch_id,user_id));
CREATE TABLE merchants.products(id uuid PRIMARY KEY,branch_id uuid NOT NULL REFERENCES merchants.branches(id),name text NOT NULL,category text NOT NULL,description text NOT NULL,image_url text,allergens text[] NOT NULL,price bigint NOT NULL CHECK(price>=0),stock integer NOT NULL CHECK(stock>=0),available boolean NOT NULL,modifiers jsonb NOT NULL);
CREATE INDEX ix_products_branch ON merchants.products(branch_id);
CREATE TABLE merchants.stock_operations(operation_id uuid PRIMARY KEY,branch_id uuid NOT NULL,lines jsonb NOT NULL,released boolean NOT NULL DEFAULT false);

