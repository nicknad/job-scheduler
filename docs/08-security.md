# Security

## Management API authentication

- **Authenticate every management API request.** The host binds the management API to **loopback by
  default** (`http://127.0.0.1:5080`); remote access requires explicit configuration.
- The credential is a **bearer token** whose value is held in the encrypted secret store and
  referenced from configuration by a **secret reference** — never a literal token in code or in a
  committed config file. The host loads the referenced token(s) at startup; a missing referenced
  token is a startup failure, not a silently open endpoint.
- **First-run bootstrap**: a configured reference with no stored value may be seeded once from the
  runtime environment variable `SCHEDULER_BOOTSTRAP_MANAGEMENT_TOKEN` (never committed). After that
  the reference resolves from the store and the bootstrap is a no-op.
- Requests carry `Authorization: Bearer <token>`. Missing or unknown tokens get **401**; a valid
  token without the required permission gets **403** with a reason. Authentication/authorization
  failures are recorded in the structured host log; the durable audit trail records lifecycle and
  secret-access decisions.
- **Each action is a separate permission.** Scopes: `read`, `install`, `validate`, `activate`,
  `deactivate`, `rollback`, `remove`, `manual-run`, `secret-admin`. Read endpoints require `read`;
  each lifecycle mutation requires its own scope; manual runs require `manual-run`; secret grant/
  revoke/set/rotate and backups require `secret-admin`.
- Long-running lifecycle operations return operation IDs and are observable
  ([09-observability-operations.md](09-observability-operations.md)).

## Idempotent lifecycle requests

- Protect against **replay or duplicate activation requests**: lifecycle mutations accept an
  operation id (the `X-Operation-Id` request header). Repeating the same id returns the previously
  recorded result instead of re-executing; a concurrent duplicate is rejected while the first is
  in flight.
- The mapping is durable, so replay after a restart still returns the recorded result.

## Least privilege and code loading

- Verify **signatures and artifact hashes before loading any code**; private signing keys live
  outside the scheduler host's writable directories.
- Run the host under a **least-privilege OS account**; restrict filesystem writes to the configured
  storage roots. Secret-store key material lives **outside every writable root** and is
  independently protected (OS-protected/ACL-restricted).
- **Reject path traversal** and unexpected files in packages before extraction; never load from
  upload directories.
- Audit lifecycle changes and secret-access **metadata** (who/what/when, allowed/denied) — never
  secret values or unredacted parameters.
- In-process execution is for **trusted, signed plugins**; runtime safety and authorization are
  never replaced by signature verification. Worker isolation is the escalation path
  ([06-secrets.md](06-secrets.md)).
