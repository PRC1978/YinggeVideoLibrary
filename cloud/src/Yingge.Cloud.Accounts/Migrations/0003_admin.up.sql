alter table accounts add column is_admin boolean not null default false;
alter table accounts add column must_change_password boolean not null default false;

