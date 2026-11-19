CREATE SCHEMA IF NOT EXISTS platform;
CREATE TABLE IF NOT EXISTS platform.schema_versions (
    version text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS platform.outbox (
    id uuid PRIMARY KEY, type text NOT NULL, payload jsonb NOT NULL,
    correlation_id text NOT NULL, created_at timestamptz NOT NULL DEFAULT now(),
    processed_at timestamptz, attempts integer NOT NULL DEFAULT 0,
    next_attempt_at timestamptz NOT NULL DEFAULT now(), last_error text
);
CREATE INDEX IF NOT EXISTS ix_outbox_pending ON platform.outbox (next_attempt_at, created_at)
WHERE processed_at IS NULL;
CREATE TABLE IF NOT EXISTS platform.inbox (
    message_id uuid NOT NULL, consumer text NOT NULL,
    processed_at timestamptz NOT NULL DEFAULT now(), PRIMARY KEY(message_id, consumer)
);
CREATE SCHEMA IF NOT EXISTS identity;
CREATE TABLE IF NOT EXISTS identity.users (
    id uuid PRIMARY KEY, email text NOT NULL UNIQUE, display_name text NOT NULL,
    terms_version text NOT NULL, terms_accepted_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE IF NOT EXISTS identity.otp_challenges (
    id uuid PRIMARY KEY, email text NOT NULL, display_name text NOT NULL,
    terms_version text NOT NULL, code_hash bytea NOT NULL,
    created_at timestamptz NOT NULL, expires_at timestamptz NOT NULL,
    attempts integer NOT NULL DEFAULT 0, consumed_at timestamptz,
    CHECK (attempts >= 0 AND attempts <= 5)
);
CREATE INDEX IF NOT EXISTS ix_otp_email_created ON identity.otp_challenges (email, created_at DESC);
CREATE TABLE IF NOT EXISTS identity.registration_audit (
    event_id uuid PRIMARY KEY, user_id uuid NOT NULL, recorded_at timestamptz NOT NULL DEFAULT now()
);
INSERT INTO platform.schema_versions(version) VALUES ('identity-001') ON CONFLICT DO NOTHING;

