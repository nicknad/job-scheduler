# Secrets

## Core rule

Centralize secret storage and authorization; retrieve values at the execution boundary.
Manifests and job configuration contain **references only**, never values.

```json
{ "jobId": "monthly-report", "secretReferences": ["reporting-db-readonly"] }
```

## Provider

Host-owned `ISecretProvider` (contract assembly) backed by an OS-protected or dedicated store:

- Windows: Credential Manager or DPAPI-backed encrypted storage.
- Linux: OS secret service, or an encrypted store with tightly controlled key access.
- Later: replaceable by Vault/cloud secret managers without changing job contracts.

A file containing encrypted secrets is not sufficient on its own: the **key must be protected
independently** and the scheduler process identity must be restricted. Never grant plugins an
unrestricted global configuration object.

## Authorization

The plugin manifest *declares* required secret references; an administrator *grants* permissions
through the policy registry. A request is checked against:

1. The authenticated execution identity.
2. The active plugin and job identity.
3. The granted secret permission.
4. The secret's current availability and version policy.

Ungranted requests fail with `SecretNotAuthorizedException` (audit the decision; never the value).

## Process boundaries

- In-process: the restricted `ISecretProvider` is an **authorization boundary, not a sandbox** —
  plugin code runs inside the host process. Intended for trusted, platform-owner-signed code.
- Worker: authenticate the worker over local IPC; authorize the worker's identity and its
  assigned job independently. Never trust a caller-supplied job ID as proof of identity.

## Rotation and caching

- New executions always use the latest authorized secret version.
- Running executions may retain an acquired credential until completion unless revocation policy
  requires interruption.
- Cache only when needed, with explicit expiration and invalidation; reinitialize connection pools
  on rotation.
- Never log secret values or include them in exception details, test data, or docs.
