# Local signed scenario catalog (S08)

The catalog is data only. Import never changes Windows policy, provider trust, settings, or recovery journals. Native policy still needs the coordinator, an independent TrustGrant and fresh capability evidence. There is no downloader, public endpoint, release signing key or remote root-update mechanism.

Use `AntigravityGeminiDns.exe --catalog-contract-test` for the isolated offline suite. To make a disposable fixture, run PowerShell 7 on .NET 10:

```powershell
./src/tools/New-LocalCatalogFixture.ps1 -AssemblyPath <built-app.dll> -OutputDirectory <new-fixture-directory> -Sequence 1
```

The generator uses .NET ECDSA P-256 and exports only its public root and signed catalog; it never writes a private key. Each generated root is explicitly for the `local-test` channel. The seed is the embedded provider catalog converted by the existing compatibility adapter, with no claim of complete application scenarios.

```text
AntigravityGeminiDns.exe --catalog-local-status <store-directory> <roots.local-test.json>
AntigravityGeminiDns.exe --catalog-local-import <store-directory> <roots.local-test.json> <catalog.local-test.json>
AntigravityGeminiDns.exe --catalog-local-rollback <store-directory> <roots.local-test.json>
AntigravityGeminiDns.exe --catalog-local-ui <store-directory> <roots.local-test.json>
```

All commands are standalone, take exact positional arguments and run without elevation. Import performs a real compiler Direct dry-run for every scenario with empty providers, grants and Windows resources. Import commits catalog data only. Status reads signed data offline.

`--catalog-local-ui` opens the normal GUI with an explicit process-only catalog source. It saves no preference. UI refresh and each Assisted preparation re-read roots and signed state, checking expiry, revocation, pending health and LKG/seed fallback. The shared fingerprint binds pack bytes, selected source, root configuration and replay floor to UI evidence and Assisted network context. A changed source invalidates old readiness/previews; the UI shows the selected pack revision. Catalog changes never automatically apply native policy.

The finite native bridge compiles the selected pack only when its requested legacy scenario exists and the transitive critical/optional dependency namespace set exactly equals the already selected core/Gemini/Google native scope. Missing scenarios, widening/shrinking scopes or capabilities other than `dns.doh` are refused explicitly. Signed metadata/probe revisions can therefore hot reload, while the existing engine's scope remains bounded. Apply revalidates catalog fingerprint and selected pack before elevated launch. Provider observations and explicit TrustGrant remain independently required. Revocation/fallback does not prevent restoring an existing journal.

The strict root document contains `Schema: 1`, `Channel: "local-test"` and `Roots`, with at most eight roots. Each root has `Id`, base64 SPKI `PublicKey`, `NotBeforeUtc`, `NotAfterUtc`, and `Revoked`. Root rotation is a deliberate local change to this explicit roots file. Add an independently checked second public root before importing its signed data. Set the old root's `Revoked` flag to reject its data, including current/LKG data. Recovery selects another still-valid signed LKG or the embedded seed. Root revocation never erases a policy recovery journal. A production root distribution/recovery ceremony needs separate authorization and is not implemented here.

The envelope is bounded to 96 KiB before JSON parsing. It contains `Manifest`, `Payload` and `Signature`. Payload is exactly `PolicyJson.Write(PackValidator.Validate(pack))`; manifest contains its uppercase SHA-256 hash. Signed bytes are UTF-8 `AGDNS-SCENARIO-CATALOG-V1\n` followed by `PolicyJson.Write(manifest)`. Signature format is exactly 64-byte IEEE P1363 ECDSA-P256/SHA-256. Unknown/duplicate fields, invalid graphs, noncanonical payloads, wrong channels, schema/compiler/client mismatches, unknown/revoked roots, expired/future data and stale/replayed sequences fail closed. Catalog lifetime is at most 90 days and must fit inside root lifetime.

Each store has a single atomic `catalog-state.json` containing current/LKG data, a monotonic high-water sequence, accepted clock floor, and pending-health flag. A per-store exclusive file handle serializes mutations. Same-directory temporary data is flushed to disk before `File.Replace` (or first-file `File.Move`). Before durable health finalization, status ignores the pending generation and selects the previous valid LKG/seed. Interruption at any checkpoint therefore cannot expose an unverified generation. Failed post-activation health restores the previous data while retaining the new replay floor. Successful LKG rollback also retains that floor. `.pending` files left by interruption are diagnostic data and never loaded. These guarantees concern process interruption and same-filesystem replacement; they do not claim storage-controller power-loss proof or atomic Windows policy changes.

A clock earlier than the last accepted time blocks imports and selects seed. Correct the clock and retry; there is no automatic expiry bypass. Corrupt state selects seed and blocks imports, preserving the corrupt file. Deliberately deleting/replacing the local state or roots is an administrative change outside the antirollback boundary. No automatic emergency downgrade/reset exists.

Official implementation references: [.NET ECDsa.VerifyData](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.ecdsa.verifydata?view=net-10.0), [File.Replace](https://learn.microsoft.com/en-us/dotnet/api/system.io.file.replace?view=net-10.0), [FileStream.Flush(Boolean)](https://learn.microsoft.com/en-us/dotnet/api/system.io.filestream.flush?view=net-10.0).
