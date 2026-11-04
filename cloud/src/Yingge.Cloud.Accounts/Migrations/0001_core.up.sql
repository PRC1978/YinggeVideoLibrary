-- 0001_core: invitations, accounts, sessions and the audit trail.
-- Invite codes and session tokens are stored as SHA-256 hashes only; the
-- plaintext never reaches this database.

create table accounts (
    id uuid primary key default gen_random_uuid(),
    username text not null,
    password_hash text not null,
    status text not null default 'active',
    created_at timestamptz not null default now(),
    password_updated_at timestamptz not null default now(),
    disabled_at timestamptz,
    failed_login_count integer not null default 0,
    locked_until timestamptz,
    constraint accounts_username_format check (
        username = lower(username) and username ~ '^[a-z0-9][a-z0-9_.-]{2,31}$'),
    constraint accounts_status_valid check (status in ('active', 'disabled')),
    constraint accounts_failed_login_nonnegative check (failed_login_count >= 0)
);

create unique index accounts_username_key on accounts (username);

create table invites (
    id uuid primary key default gen_random_uuid(),
    code_hash bytea not null unique,
    note text,
    created_at timestamptz not null default now(),
    expires_at timestamptz not null,
    used_at timestamptz,
    used_by_account uuid references accounts (id) on delete set null,
    revoked_at timestamptz,
    constraint invites_expiry_after_creation check (expires_at > created_at),
    constraint invites_note_length check (note is null or char_length(note) <= 200)
);

create index invites_pending_idx on invites (expires_at)
    where used_at is null and revoked_at is null;

create table sessions (
    id uuid primary key default gen_random_uuid(),
    account_id uuid not null references accounts (id) on delete cascade,
    token_hash bytea not null unique,
    created_at timestamptz not null default now(),
    expires_at timestamptz not null,
    last_seen_at timestamptz,
    revoked_at timestamptz,
    client_version text,
    created_ip text,
    constraint sessions_expiry_after_creation check (expires_at > created_at)
);

create index sessions_account_idx on sessions (account_id, created_at desc);
create index sessions_expiry_idx on sessions (expires_at) where revoked_at is null;

create table audit_events (
    id bigserial primary key,
    at timestamptz not null default now(),
    event text not null,
    account_id uuid,
    ip text,
    detail jsonb not null default '{}'::jsonb
);

create index audit_events_at_idx on audit_events (at desc);
create index audit_events_account_idx on audit_events (account_id, at desc);
