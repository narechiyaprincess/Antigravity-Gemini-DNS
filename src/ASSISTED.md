# Assisted coordinator

The same `AssistedCoordinator` is used by main/tray/scope UI actions and the bounded CLI. Engine continues to own Windows writes, protected staging, its native journal and the cross-process mutation lock. This path requires no broker, resolver, global proxy or durable scenario lease. Native Windows apply/restore acceptance is separate from offline verification. When the GUI already has administrator rights, its bounded automatic repair can reapply the current bridge; an ordinary process does not request UAC without user action.

```
AntigravityGeminiDns.exe --assisted-do-nothing
AntigravityGeminiDns.exe --assisted-observe
AntigravityGeminiDns.exe --assisted-inspect
AntigravityGeminiDns.exe --assisted-direct
AntigravityGeminiDns.exe --assisted-connect --profile geohide
AntigravityGeminiDns.exe --assisted-connect --profile geohide --trust-proxy
AntigravityGeminiDns.exe --assisted-native --profile geohide --trust-proxy
AntigravityGeminiDns.exe --assisted-disable
AntigravityGeminiDns.exe --assisted-contract-test
```

`--assisted-do-nothing` performs no backend work. Observe/inspect are read-only with respect to Windows DNS policy, though normal verified script staging may create application files. Direct uses the S03 bounded system DNS/TCP/TLS/HEAD path only when no owned active or conflicting policy exists. A pass claims **IPv4 transport only**, through the current OS/VPN/TUN, and never account/model/stream success. An active bridge is never called Direct. IPv6 and browser/app resolver attribution retain independent UNKNOWN outcomes. Auto tries Direct first; Native explicitly requests native policy. Redirect/uncertain network evidence suspends fallback rather than diagnosing a captive portal or repairing Windows.

CLI native changes require `--trust-proxy`; without it, preparation can inspect the network but cannot apply. CLI profiles must be embedded catalog IDs/aliases. Main UI supports existing custom HTTPS providers, including nondefault HTTPS ports, and revalidates saved addresses before bootstrap as before. UI consent `CatalogProvidersV1` covers the embedded catalog and its supported scopes; a custom endpoint retains an exact endpoint/scope grant. Revocation clears both. Historical name-only trust values do not authorize these connections. The UI obtains consent before starting the candidate deadline, then creates a scoped grant for the validated preview. Replacement of an existing route is stated in the dialog.

Preparation captures network events/snapshot, complete native context and bounded preimage. It compiles one existing legacy scope and one DoH provider with fresh DNS/DoH/TLS proof. The compiler remains pure. Existing schema-1 journal ownership is one exclusive legacy selection, not a collection of durable leases: the adapter verifies schema, recognized old scope, complete owned rule set and matching fingerprints, then explicitly releases that selection in the semantic compiler request. The full immutable native owner/GUID/fingerprint snapshot remains authoritative for Engine. All nine core/Gemini/Google transitions cover Add/Replace/Remove. A compiler Keep is a semantic route decision; the old native backend can still restore/recreate owned resources during a scope change. No foreign or durable lease is silently released.

After explicit trust, preview validation checks the exact immutable request, capabilities, grant, native context, network epoch and five-minute expiry again. A final cancellation/intent check occurs after the last awaited capture and before Apply/Disable. The elevated child receives a canonical native revision plus expiry, which Engine rechecks under its existing `operation.lock`, after provider preflight and before the first journal/write phase. A changed validated pin subset requires a new preview. Engine writes Applying/Restoring to its protected journal before DNS mutations. An absent/malformed/expired/stale preimage fails closed; no automatic retry with another route is attempted.

Operations are serialized per coordinator; Engine's protected lock remains the machine mutation lock. Manual off changes intent immediately, supersedes pre-mutation connects and suppresses stale health/failover offers. Observe/health never enable a route. A later explicit user connect can create a new intent. Disable depends on existing native ownership, not on the selected provider being valid/reachable. A failure after possible writes is reconciled against the captured semantic baseline and original foreign resource fingerprints, using existing Engine compensation. The coordinator either proves baseline restoration or returns **RecoveryRequired**. It does not destroy an original active profile to imitate an empty rollback.

UAC refusal and cancellation before process creation are no-change results. A timed-out/cancelled elevated child, missing matching operation receipt, or uncertain recovery blocks further mutations in that coordinator; it never starts parallel restore while a child may still run. After possible mutation, caller cancellation cannot become “cancelled with no changes.” Verification requires current native policy plus Windows resolver diagnosis for the exact selected route, separate from app readiness. Ownership-off, restored-baseline and transport-only proofs cannot fill this native level. The proof explicitly does not establish Windows wire encryption, complete service operation, or authenticated app behavior.

Compatibility boundaries: legacy `--enable`/`--restore` still exist; the coordinator passes additional revision/expiry guards through those same protected paths. Evidence reduction, compiler preview validation, probe limits and resource ownership checks are preserved. Restore retains ambiguous Pending DoH registrations even when their values match; matching values alone do not prove ownership.

Two known implementation limitations remain. The native Engine revision includes all adapters, whereas the C# network epoch filters route-irrelevant changes; an unrelated adapter change can therefore still cause a safe stale-preview refusal. Also, the applied-receipt fast path compares the granted preview with the original ungranted preview, so the ordinary coordinator path continues to perform full diagnosis. The static receipt-matcher tests do not establish a connection speed improvement.

Verification must distinguish:

- C# coordinator fixtures using the real pure compiler and injected faults.
- `AssistedNative.Tests.ps1`: actual Engine code, protected journal file IO and on-disk operation lock, **all Windows mutation cmdlets mocked**.
- A read-only native snapshot on the working host, with before/after fingerprints.
- Real Windows apply/restore/foreign-state preservation in a separately authorized isolated lab. Neither of the first three proves this last gate.

Build/offline/rendering acceptance does not establish real apply/restore, UAC, logon, crash/reboot or application-account readiness. A local release report must identify the exact tested binary and distinguish those unrun gates. Packaging a separate delivery must not replace the owner's running EXE or shortcut.
