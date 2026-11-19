CREATE SCHEMA ledger;
CREATE TABLE ledger.vouchers(id uuid PRIMARY KEY,order_id uuid NOT NULL,kind text NOT NULL,currency text NOT NULL,created_at timestamptz NOT NULL DEFAULT now());
CREATE TABLE ledger.lines(id uuid PRIMARY KEY,voucher_id uuid NOT NULL REFERENCES ledger.vouchers(id),account text NOT NULL,amount bigint NOT NULL CHECK(amount<>0),created_at timestamptz NOT NULL DEFAULT now());
CREATE INDEX ix_ledger_accounts ON ledger.lines(account,created_at);
CREATE TABLE ledger.deliveries(order_id uuid PRIMARY KEY,merchant_id uuid NOT NULL,courier_id uuid NOT NULL,total bigint NOT NULL,merchant_amount bigint NOT NULL,courier_amount bigint NOT NULL,platform_amount bigint NOT NULL,refunded bigint NOT NULL DEFAULT 0);
CREATE TABLE ledger.settlements(id uuid PRIMARY KEY,account text NOT NULL,currency text NOT NULL,amount bigint NOT NULL CHECK(amount>0),closed_at timestamptz NOT NULL DEFAULT now(),paid_at timestamptz,external_reference text);
CREATE TABLE ledger.settlement_lines(line_id uuid PRIMARY KEY REFERENCES ledger.lines(id),settlement_id uuid NOT NULL REFERENCES ledger.settlements(id));
CREATE FUNCTION ledger.check_balance() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF (SELECT COALESCE(sum(amount),0) FROM ledger.lines WHERE voucher_id=NEW.voucher_id)<>0 THEN RAISE EXCEPTION 'Unbalanced voucher'; END IF;
 RETURN NULL;
END; $$;
CREATE CONSTRAINT TRIGGER ledger_balance AFTER INSERT ON ledger.lines DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION ledger.check_balance();
CREATE FUNCTION ledger.immutable_line() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'Ledger entries are immutable'; END; $$;
CREATE TRIGGER immutable_ledger BEFORE UPDATE OR DELETE ON ledger.lines FOR EACH ROW EXECUTE FUNCTION ledger.immutable_line();

