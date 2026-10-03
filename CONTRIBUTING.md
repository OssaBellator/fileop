# Contributing

FileOp changes should preserve its core rule: **evidence is not mutation authority**.

## Expectations

- Keep the normal desktop process non-elevated.
- Preserve fresh filesystem identity/path validation around mutations.
- Do not turn Storage/Optimize recommendations, hashes, or readiness checks into deletion consent.
- Add regression coverage for changes to indexing, IPC, recovery history, privilege, Copy/Move, or delete boundaries.
- Document platform/release assumptions that still require real Windows validation.

The authoritative local validation entry point is:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/test-local.ps1
```

For offline model/source verification:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/test-local.ps1 -OfflineOnly
```

See [docs/local-validation.md](./docs/local-validation.md) and [SECURITY.md](./SECURITY.md).
