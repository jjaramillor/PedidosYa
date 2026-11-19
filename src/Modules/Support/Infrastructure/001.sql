CREATE SCHEMA support;
CREATE TABLE support.cases(id uuid PRIMARY KEY,order_id uuid NOT NULL,opened_by uuid NOT NULL,assigned_to uuid,subject text NOT NULL,description text NOT NULL,state text NOT NULL CHECK(state IN('open','investigating','resolved','closed')),due_at timestamptz NOT NULL,created_at timestamptz NOT NULL DEFAULT now(),resolution text);
CREATE TABLE support.evidence(case_id uuid NOT NULL REFERENCES support.cases(id),document_id uuid NOT NULL,uploaded_by uuid NOT NULL,PRIMARY KEY(case_id,document_id));
CREATE TABLE support.notes(id uuid PRIMARY KEY,case_id uuid NOT NULL REFERENCES support.cases(id),actor_id uuid NOT NULL,message text NOT NULL,created_at timestamptz NOT NULL DEFAULT now());

