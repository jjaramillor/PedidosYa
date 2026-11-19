CREATE SCHEMA notifications;
CREATE TABLE notifications.messages(id uuid PRIMARY KEY,event_id uuid NOT NULL,user_id uuid NOT NULL,order_id uuid NOT NULL,title text NOT NULL,body text NOT NULL,created_at timestamptz NOT NULL DEFAULT now(),read_at timestamptz,UNIQUE(event_id,user_id));

