# Bundled PostgreSQL runtime: development status

The installer is a per-user Inno Setup package. PostgreSQL can be carried as
application files without registering a machine-wide Windows service. The
runtime is **not enabled in normal app startup yet**; JSON remains authoritative.

## Pinned build input

- PostgreSQL 18.6, EDB Windows x64 binaries, archive `postgresql-18.6-5-windows-x64-binaries.zip`.
- Download link: <https://sbp.enterprisedb.com/getfile.jsp?fileid=1260609>.
- SHA-256: `E2246BA91D22345BC3D017586C09EDE52D9DF180B1EEB480F050445F1CAD84E2`.
- Run `scripts/prepare-postgres-runtime.ps1 -Download` to verify and stage the
  runtime under ignored `artifacts/postgres-runtime`. The script extracts only
  `bin`, `lib`, `share` and server/third-party license notices. It refuses an
  archive with a different hash or an existing runtime with a different manifest.
- The staged runtime was inspected on Windows: `postgres --version` reported
  `18.6`; 1,586 files / 147,931,946 bytes including its manifest. No user data
  was accessed. The preparation script was rerun and returned `Already staged`.

The Inno Setup source has an **opt-in** `BundlePostgres` preprocessor flag. It
must remain off for ordinary 1.6.6 packages. Turning it on copies the staged
runtime into `{app}\postgres` but does **not** change where the library is read
or create a database cluster. Shipping that variant is not ready.

## Required before enabling the flag in a release

1. Start and stop a private cluster in an isolated data directory with local
   authentication and a password stored in Windows Credential Manager. Bind
   only to loopback; prevent a second instance from creating a second server.
2. Move every authoritative writer and the backup/restore path to one coherent
   database transaction model. Current JSON review, config, queue and person
   writers cannot be left behind while the index alone moves.
3. Rehearse migration, restart, backup, restore, update and rollback with an
   isolated library. Preserve the old JSON files until the database is verified.
4. Define major-version upgrade handling with a logical backup/restore. Do not
   start a new PostgreSQL major version on an old cluster's data directory.

The first combined shell probe was rejected by automatic approval review; no
process ran from that command. A narrower, credential-free isolation probe
then initialized and started PostgreSQL with all logins rejected. After that,
`--self-test-bundled-postgres` verified the product's process code using a
fresh isolated cluster: PostgreSQL 18.6 answered over `127.0.0.1`, the random
password round-tripped through Windows Credential Manager, and `pg_ctl` stopped
the server. The probe also imported and compared one synthetic video and its
metadata through the real migration code (`sampleImportVerified=true`). The
first product probe exposed an inherited-output-pipe wait;
that was fixed before the successful run. `realLibraryRead=false` in the final
result. This tests the runtime mechanism, **not** production migration or the
Inno Setup installation path.
