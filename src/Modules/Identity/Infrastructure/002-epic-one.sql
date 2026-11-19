ALTER TABLE identity.users ADD COLUMN status text NOT NULL DEFAULT 'active'
    CHECK(status IN ('active','deactivated','blocked','closed'));
ALTER TABLE identity.otp_challenges ADD COLUMN device_name text NOT NULL DEFAULT 'unknown';
ALTER TABLE identity.otp_challenges ADD COLUMN privacy_version text NOT NULL DEFAULT '2026-10';
-- Legacy challenges did not capture explicit privacy acceptance; require a fresh registration request.
UPDATE identity.otp_challenges SET consumed_at=now() WHERE consumed_at IS NULL;
UPDATE platform.outbox SET processed_at=now(),payload='{}'
    WHERE type='identity.otp-requested.v1' AND processed_at IS NULL;
ALTER TABLE identity.users ADD COLUMN updated_at timestamptz;
ALTER TABLE identity.otp_challenges ADD COLUMN purpose text NOT NULL DEFAULT 'register'
    CHECK(purpose IN ('register','login','recovery'));
ALTER TABLE identity.otp_challenges ADD COLUMN eligible boolean NOT NULL DEFAULT true;
CREATE TABLE identity.sessions (
    id uuid PRIMARY KEY, user_id uuid NOT NULL REFERENCES identity.users(id),
    created_at timestamptz NOT NULL, expires_at timestamptz NOT NULL,
    revoked_at timestamptz, revoke_reason text, device_name text NOT NULL DEFAULT 'unknown',
    last_used_at timestamptz NOT NULL
);
CREATE INDEX ix_sessions_user ON identity.sessions(user_id);
CREATE TABLE identity.refresh_tokens (
    id uuid PRIMARY KEY, session_id uuid NOT NULL REFERENCES identity.sessions(id),
    token_hash bytea NOT NULL UNIQUE, created_at timestamptz NOT NULL, expires_at timestamptz NOT NULL, used_at timestamptz
);
CREATE INDEX ix_refresh_session ON identity.refresh_tokens(session_id);
CREATE TABLE identity.permissions (id text PRIMARY KEY);
INSERT INTO identity.permissions(id) VALUES ('identity.roles.manage'),('identity.users.manage'),('identity.audit.read');
CREATE TABLE identity.roles (id text PRIMARY KEY, name text NOT NULL, system_role boolean NOT NULL DEFAULT false);
INSERT INTO identity.roles(id,name,system_role) VALUES ('customer','Cliente',true),('admin','Administrador',true);
CREATE TABLE identity.role_permissions (
    role_id text NOT NULL REFERENCES identity.roles(id), permission_id text NOT NULL REFERENCES identity.permissions(id),
    PRIMARY KEY(role_id,permission_id)
);
INSERT INTO identity.role_permissions(role_id,permission_id) SELECT 'admin',id FROM identity.permissions;
CREATE TABLE identity.user_roles (
    user_id uuid NOT NULL REFERENCES identity.users(id), role_id text NOT NULL REFERENCES identity.roles(id),
    PRIMARY KEY(user_id,role_id)
);
INSERT INTO identity.user_roles(user_id,role_id) SELECT id,'customer' FROM identity.users;
CREATE TABLE identity.consent_history (
    sequence bigint GENERATED ALWAYS AS IDENTITY,
    id uuid PRIMARY KEY, user_id uuid NOT NULL REFERENCES identity.users(id), kind text NOT NULL,
    version text NOT NULL, accepted boolean NOT NULL, recorded_at timestamptz NOT NULL,
    CHECK(kind IN ('terms','privacy','marketing'))
);
CREATE INDEX ix_consents_user ON identity.consent_history(user_id,recorded_at DESC);
INSERT INTO identity.consent_history(id,user_id,kind,version,accepted,recorded_at)
    SELECT id,id,'terms',terms_version,true,terms_accepted_at FROM identity.users;
CREATE TABLE identity.security_audit (
    id uuid PRIMARY KEY, actor_id uuid, subject_id uuid, action text NOT NULL,
    data jsonb NOT NULL DEFAULT '{}', correlation_id text NOT NULL, occurred_at timestamptz NOT NULL
);
CREATE INDEX ix_security_audit_time ON identity.security_audit(occurred_at DESC);
CREATE TABLE identity.federated_flows (
    id uuid PRIMARY KEY, state_hash bytea NOT NULL UNIQUE, nonce text NOT NULL,
    protected_verifier text NOT NULL, display_name text NOT NULL,
    terms_version text NOT NULL, privacy_version text NOT NULL, expires_at timestamptz NOT NULL, consumed_at timestamptz
);
CREATE TABLE identity.external_identities (
    issuer text NOT NULL, subject text NOT NULL, user_id uuid NOT NULL REFERENCES identity.users(id),
    PRIMARY KEY(issuer,subject)
);
CREATE TABLE identity.event_receipts (
    event_id uuid PRIMARY KEY, event_type text NOT NULL, payload jsonb NOT NULL,
    processed_at timestamptz NOT NULL DEFAULT now()
);
