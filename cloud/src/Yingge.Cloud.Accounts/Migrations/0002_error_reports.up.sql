-- 0002_error_reports: metadata for uploaded error reports. The report body
-- lives on disk outside the web root; this table only records what is needed
-- for quota, retention and the user's own delete action.

create table error_reports (
    id uuid primary key,
    account_id uuid not null references accounts (id) on delete cascade,
    received_at timestamptz not null default now(),
    client_version text not null,
    os text not null,
    arch text not null,
    occurred_at timestamptz,
    message_count integer not null,
    byte_size integer not null,
    content_hash bytea not null,
    storage_key text not null,
    retention_expires_at timestamptz not null,
    deleted_at timestamptz,
    deleted_reason text,
    constraint error_reports_message_count_range check (message_count between 1 and 100),
    constraint error_reports_byte_size_range check (byte_size > 0 and byte_size <= 262144)
);

-- A repeated submission of the same error set is rejected instead of stored
-- twice. Deleting a report frees the hash so the user can upload it again.
create unique index error_reports_dedupe_key on error_reports (account_id, content_hash)
    where deleted_at is null;

create index error_reports_account_received_idx on error_reports (account_id, received_at desc);
create index error_reports_retention_idx on error_reports (retention_expires_at)
    where deleted_at is null;
