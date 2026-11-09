-- One small, private PNG per account. Avatars are never part of error reports.
create table account_avatars (
    account_id uuid primary key references accounts (id) on delete cascade,
    image_png bytea not null,
    updated_at timestamptz not null default now(),
    constraint account_avatar_size check (octet_length(image_png) between 1 and 245760)
);
