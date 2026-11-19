CREATE SCHEMA payments;
CREATE TABLE payments.payments(id uuid PRIMARY KEY,order_id uuid NOT NULL UNIQUE,customer_id uuid NOT NULL,amount bigint NOT NULL CHECK(amount>=0),currency text NOT NULL,method text NOT NULL CHECK(method IN('card','cash')),state text NOT NULL,provider_id text,client_secret text,captured bigint NOT NULL DEFAULT 0,created_at timestamptz NOT NULL DEFAULT now(),updated_at timestamptz NOT NULL DEFAULT now());
CREATE TABLE payments.refunds(id uuid PRIMARY KEY,payment_id uuid NOT NULL REFERENCES payments.payments(id),amount bigint NOT NULL CHECK(amount>0),state text NOT NULL,reason text NOT NULL,actor_id uuid NOT NULL,created_at timestamptz NOT NULL DEFAULT now());
CREATE TABLE payments.operations(id uuid PRIMARY KEY,payment_id uuid NOT NULL,kind text NOT NULL,completed boolean NOT NULL DEFAULT false);
CREATE TABLE payments.reconciliations(id uuid PRIMARY KEY,payment_id uuid NOT NULL,local_state text NOT NULL,provider_state text NOT NULL,matches boolean NOT NULL,checked_at timestamptz NOT NULL DEFAULT now());

