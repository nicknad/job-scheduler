# Security

- **Authenticate every management API request**; authorize installation, activation, removal,
  secret administration, and manual execution as **separate** permissions.
- **Bind management endpoints to loopback by default**; remote access only via explicit
  configuration.
- Verify **signatures and artifact hashes before loading any code**; private signing keys live
  outside the scheduler host's writable directories.
- Run the host under a **least-privilege OS account**; restrict filesystem writes to the configured
  storage roots.
- **Reject path traversal** and unexpected files in packages before extraction; never load from
  upload directories.
- Audit lifecycle changes and secret-access **metadata** (who/what/when, allowed/denied) — never
  secret values or unredacted parameters.
- Protect against **replay or duplicate activation requests**: idempotent, operation-scoped
  commands with operation IDs.
- Long-running lifecycle operations return operation IDs and are observable
  ([09-observability-operations.md](09-observability-operations.md)).
- In-process execution is for **trusted, signed plugins**; runtime safety and authorization are
  never replaced by signature verification. Worker isolation is the escalation path
  ([06-secrets.md](06-secrets.md)).
