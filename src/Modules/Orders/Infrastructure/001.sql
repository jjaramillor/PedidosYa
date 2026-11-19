CREATE SCHEMA orders;
CREATE TABLE orders.orders(id uuid PRIMARY KEY,customer_id uuid NOT NULL,branch_id uuid NOT NULL,state text NOT NULL,quote jsonb NOT NULL,method text NOT NULL,protected_token text,request_hash text NOT NULL,accept_by timestamptz,created_at timestamptz NOT NULL DEFAULT now(),updated_at timestamptz NOT NULL DEFAULT now());
CREATE INDEX ix_orders_customer ON orders.orders(customer_id,created_at DESC);
CREATE INDEX ix_orders_branch ON orders.orders(branch_id,state);
CREATE TABLE orders.transitions(id uuid PRIMARY KEY,order_id uuid NOT NULL REFERENCES orders.orders(id),actor_id uuid NOT NULL,from_state text NOT NULL,to_state text NOT NULL,reason text NOT NULL,occurred_at timestamptz NOT NULL DEFAULT now());
CREATE TABLE orders.operations(id uuid PRIMARY KEY,order_id uuid NOT NULL,state text NOT NULL);

