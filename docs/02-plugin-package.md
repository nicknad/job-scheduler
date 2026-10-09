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

## Required manifest fields

| Field | Requirement |
| --- | --- |
| `id` | Stable, unique plugin identifier |
| `version` | Immutable plugin version |
| `contractVersion` | Supported host contract version (must be compatible with the host's) |
| `entryAssembly` | Relative path to the main assembly |
| `entryType` | Fully qualified plugin entry type implementing `IJobPlugin` |
| `executionMode` | `in-process` or `worker` |
| `capabilities` | Declared host services and permissions **requested** |
| `dependencies` | Plugin-specific dependency info |
| `artifactHash` | Cryptographic digest of the artifact |
| `signature` | Signature covering the canonical manifest representation and the package digest |

The manifest's `capabilities` are **requests, not grants**. Actual permissions come from the
host's authorization policy ([06-secrets.md](06-secrets.md)).

## Validation gates (all must pass before staging succeeds)

1. **Archive safety**: reject path traversal (`..`, absolute paths, drive letters, symlinked/odd
   entries), unexpected files, duplicate manifest entries, and archives that unpack beyond size
   limits. Extract into a staging root, never the upload directory.
2. **Manifest**: well-formed JSON, all required fields present and well-shaped, version parses,
   `contractVersion` compatible with the host contract (explicit compatibility decision — no
   silent major-version acceptance).
3. **Digest**: recompute the package digest over actual bytes and compare to `artifactHash`.
4. **Signature**: verify the signature over a *canonical* serialization of the manifest plus the
   package digest against the platform owner's public key. Reject on any mismatch.
5. **Assembly level checks**: entry assembly exists, `entryType` implements `IJobPlugin`, and the
   plugin declares no dependencies that resolve outside its own package or the approved shared
   host dependencies (the contract assembly is always resolved from the host — plugins must not
   bundle a copy).

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
