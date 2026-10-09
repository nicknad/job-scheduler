# Secrets

## Core rule

Centralize secret storage and authorization; retrieve values at the execution boundary.
Manifests and job configuration contain **references only**, never values.

```json
{ "jobId": "monthly-report", "secretReferences": ["reporting-db-readonly"] }
```

## Store

Values live in a host-owned encrypted store behind the application port `ISecretValueStore`
(`Scheduler.Application.Secrets`); the infrastructure implementation is `FileSecretValueStore`.

- Each value is encrypted independently with AES-256-GCM; the file records the reference plus the
  nonce, tag, and ciphertext, and the reference is bound as additional authenticated data so a
  swapped envelope cannot be substituted for another reference. The store exposes
  `Get`/`Set`/`Remove`/`List` over references. Writes are atomic (temp file + rename).
- The 256-bit key lives in a **separate key file outside every writable root** (artifacts, staging,
  data, logs), enforced at startup by `SecretKeyPathGuard`, mirroring signing-key hygiene. The key is
  the independently protected half: on Windows the key file is intended to be OS-protected (DPAPI or
  an ACL restricted to the service identity); on Linux it is protected by least-privilege file
  permissions. The process identity is least-privilege.
- Values are **never** written to the registry database, the artifact store, logs, audit entries,
  execution result summaries, per-execution logs, tests, or docs. References are not secret.
- The provider stays replaceable (Vault/cloud managers) without changing job contracts; no such
  adapter is built now.

## Grants

An administrator *grants* secret references to a plugin (optionally narrowed to a single job)
through a durable policy registry. The plugin manifest/job *declares* the references it may request.

- Durable `secret_grants` rows: `(plugin_id, job_id, secret_reference, granted_by, granted_at)`; an
  empty `job_id` is a plugin-wide grant.
- Granting is idempotent (upsert); revoking removes the row. Both are audited
  (`secret.grant` / `secret.revoke`) with references and metadata only.
- The store holds values; the registry holds grants. Neither the registry nor the artifact store
  ever holds a value.

## Per-execution provider

`JobExecutionContext.Secrets` is created **per execution** by `ISecretProviderFactory`
(`Scheduler.Application.Secrets`), just like the execution-scoped logger. The factory is handed the
`ExecutionIdentity` (execution id, correlation id, job id, plugin id, plugin version) and reads the
granted references for that `(plugin, job)` pair.

- The resulting `GrantedSecretProvider` answers only for granted references; anything else fails
  with `SecretNotAuthorizedException` and is audited as denied.
- Resolution success is audited as allowed (the first resolution per execution; a cached value is
  not re-audited). Audit entries carry the actor (the execution), the
  reference, and plugin/job metadata — never the value.
- A granted-but-missing value is denied (audited with `reason=unavailable`, never a value); a value
  is never invented.

## Process boundaries

- In-process: the restricted `ISecretProvider` is an **authorization boundary, not a sandbox** —
  plugin code runs inside the host process. Intended for trusted, platform-owner-signed code.
- Worker: authenticate the worker over local IPC; authorize the worker's identity and its
  assigned job independently. Never trust a caller-supplied job ID as proof of identity.

## Rotation and caching

- New executions always use the latest authorized value (resolve-at-dispatch).
- A running execution retains the value it acquired for the lifetime of the execution; the
  per-execution provider caches resolved values, and rotation does not change them mid-flight.
- Revoking a grant is the explicit invalidation path: it takes effect for new executions
  immediately. A revocation policy may interrupt running executions; the default retains
  in-flight values until completion.
- Nested caches beyond the execution scope are avoided; reinitialize connection pools on rotation.
- Never log secret values or include them in exception details, test data, or docs.
