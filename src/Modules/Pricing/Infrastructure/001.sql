CREATE SCHEMA pricing;
CREATE TABLE pricing.carts(user_id uuid PRIMARY KEY,branch_id uuid NOT NULL,lines jsonb NOT NULL,updated_at timestamptz NOT NULL DEFAULT now());
CREATE TABLE pricing.coupons(id uuid PRIMARY KEY,code text NOT NULL UNIQUE,branch_id uuid,starts_at timestamptz NOT NULL,ends_at timestamptz NOT NULL,minimum_order bigint NOT NULL CHECK(minimum_order>=0),discount_bps integer NOT NULL CHECK(discount_bps BETWEEN 1 AND 10000),maximum_discount bigint NOT NULL CHECK(maximum_discount>0),total_limit integer NOT NULL CHECK(total_limit>0),per_customer_limit integer NOT NULL CHECK(per_customer_limit>0),merchant_funded boolean NOT NULL,active boolean NOT NULL);
CREATE TABLE pricing.quotes(id uuid PRIMARY KEY,user_id uuid NOT NULL,snapshot jsonb NOT NULL,consumed_by uuid UNIQUE,expires_at timestamptz NOT NULL);
CREATE TABLE pricing.coupon_uses(operation_id uuid PRIMARY KEY,coupon_id uuid NOT NULL REFERENCES pricing.coupons(id),customer_id uuid NOT NULL,released boolean NOT NULL DEFAULT false);

