# Plugin Package

## Layout

A plugin is distributed as an immutable archive (e.g. ZIP) containing assemblies, dependencies, and
a manifest. Reference layout:

```
monthly-report/
├── plugin.json          manifest (below)
├── MonthlyReport.dll
├── MonthlyReport.deps.json
├── dependencies/
└── signature.json
```

The archive is the package. `plugin.json` is the manifest; `signature.json` carries the detached
signature (schema below). No file is optional except `dependencies/` when the plugin has none.

## Required manifest fields

| Field | Requirement |
| --- | --- |
| `id` | Stable, unique plugin identifier |
| `version` | Immutable plugin version |
| `contractVersion` | Supported host contract version (must be compatible with the host's) |
| `entryAssembly` | Relative path to the main assembly |
| `entryType` | Fully qualified plugin entry type implementing `IJobPlugin` |
| `executionMode` | `in-process` or `worker` |
| `capabilities` | Declared host services and permissions **requested** (optional; defaults to empty) |
| `dependencies` | Plugin-specific dependency info (optional; defaults to empty) |
| `artifactHash` | Canonical package digest (`sha256:<hex>`, see below) |

The manifest's `capabilities` are **requests, not grants**. Actual permissions come from the
host's authorization policy ([06-secrets.md](06-secrets.md)).

The signature is **not** a manifest field; it is carried by `signature.json` and is excluded from
the canonical manifest (below).

## Canonical form, digest, and signature

These byte-level definitions are normative: the signer and the verifier must produce and consume
exactly these bytes, and they are covered by acceptance criterion #2.

- **Canonical manifest.** `plugin.json` serialized as UTF-8 without a BOM, compact (no
  insignificant whitespace), object keys in ordinal-sorted order, fixed wire values for enums
  (`executionMode` is `"in-process"` or `"worker"`), arrays in declaration order. It contains
  every required field above and never a signature.
- **Canonical package digest (`artifactHash`).** SHA-256 over the canonical entry digest — a
  deterministic, archive-format-independent encoding of every archive entry **except `plugin.json`
  and `signature.json`**. The manifest is excluded because it carries the digest (it would be
  self-referential) and is instead protected by the signature; the signature is added last.
  Entries are ordered by ordinal path; each contributes
  `UTF-8(path) + '\0' + invariant(lengthBytes) + '\0' + sha256hex(contentBytes) + '\n'`, with
  directory entries omitted. Written as `sha256:<lowercase hex>`.
- **Signature input.** `canonical-manifest-bytes || raw-32-byte-package-digest`. The signature is
  over this concatenation and is stored, base64-encoded, in `signature.json`:

  ```json
  { "formatVersion": 1, "algorithm": "RS256", "signature": "<base64>" }
  ```

- **Algorithms.** `RS256` (RSA PKCS#1 v1.5, SHA-256) and `ES256` (ECDSA P-256, SHA-256). The key
  type is detected from the configured PEM public key; `algorithm` must match it.
- **Signing key.** A PEM-encoded public key at a configured path that **must be outside every
  writable storage root**; this is enforced at startup. Private keys never live in the host's
  writable directories.

## Validation gates (all must pass before staging succeeds)

1. **Archive safety**: reject path traversal (`..`, absolute paths, drive letters, symlinked/odd
   entries), non-regular entries, unexpected files, duplicate entries, and archives that exceed
   the configured entry-count, per-entry/total uncompressed-size, or compression-ratio limits.
   Extract into a staging root, never the upload directory.
2. **Manifest**: well-formed JSON, all required fields present and well-shaped, version parses,
   `contractVersion` compatible with the host contract (explicit compatibility decision — no
   silent major-version acceptance).
3. **Digest**: recompute the package digest over actual bytes and compare to `artifactHash`.
4. **Signature**: verify the signature over a *canonical* serialization of the manifest plus the
   package digest against the platform owner's public key. Reject on any mismatch.
5. **Assembly level checks (install)**: the entry assembly exists in the package and the entry
   type is declared. Confirming `entryType` implements `IJobPlugin` and that dependencies resolve
   only within the package or approved host assemblies requires loading the assembly, which happens
   at activation ([03-plugin-lifecycle.md](03-plugin-lifecycle.md)) — plugins must never bundle a
   copy of the contract assembly, which is always resolved from the host.

Persist the validation result (valid/invalid + reasons) in the registry before promotion.
Invalid packages are rejected without affecting any active version.

## Versioning rules

- A published version is immutable; any code change produces a **new version**.
- Plugin identity (`id`) is stable across versions; the host contract is versioned independently.
- Breaking contract changes require an explicit compatibility decision recorded as an ADR.
- Plugin assemblies are **never** loaded directly from an upload directory — only from the
  immutable artifact store after validation.

## Artifact store

Content-addressed layout under the artifacts root: `artifacts/{pluginId}/{version}/…`, immutable
after promotion, retained per the rollback/audit retention policy before physical deletion.
