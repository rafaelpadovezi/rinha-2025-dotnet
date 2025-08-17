CREATE UNLOGGED TABLE payments (
    correlation_id uuid PRIMARY KEY,
    amount DECIMAL(10, 2) NOT NULL,
    requested_at TIMESTAMPTZ NOT NULL,
    processor int NOT NULL
);

CREATE INDEX payments_idx ON payments (processor, requested_at);