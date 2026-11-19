ALTER TABLE payments.reconciliations ADD COLUMN local_amount bigint;
ALTER TABLE payments.reconciliations ADD COLUMN provider_amount bigint;
ALTER TABLE payments.reconciliations ADD COLUMN local_refunded bigint;
ALTER TABLE payments.reconciliations ADD COLUMN provider_refunded bigint;
