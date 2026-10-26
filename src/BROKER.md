# Optional broker and recovery

Native Assisted continues to work through its existing explicit UAC path without an installed service. The optional `AntigravityDnsBrokerV1` service provides finite durable native leases and recovery independently of the GUI. It does not run a local DNS listener or enable Autopilot.

Install and uninstall are explicit elevated operations using a published single-file candidate:

```text
--broker-install
--broker-status
--broker-acquire geohide.ru core 10 --trust-proxy
--broker-release <lease-id>
--broker-recover
--broker-uninstall
```

Scopes are `core`, `gemini`, and `google`; profiles must be exact embedded catalog IDs. Lease durations are finite, 1–1440 minutes. Acquire returns its lease ID. Status is available to ordinary local users; mutation requires an elevated Administrator token. The account/session bound to the actual IPC token owns its lease references. A second matching reference shares the existing native configuration; a conflicting configuration or a manual Assisted bridge is refused. The last released or expired reference triggers owned-only restoration. Service startup recovers interrupted managed changes; a five-second supervision loop handles expiration. SCM restarts a failed broker. Recovery is also available from the protected retained candidate with `--broker-recover-local`, without starting the GUI.

Uninstall drains the service, runs its installed recovery code and requires an absent/restored journal. Active leases or an uncertain restore cause refusal and restart independent recovery. Journals, receipts and the protected recovery candidate are retained. Reinstallation of the same verified candidate is supported only with a quiescent compatible journal; a different retained candidate is refused rather than overwriting its recovery code.

The protocol is version 1, local named pipe `AntigravityDnsUnlocker.Broker.v1`, explicit protected DACL and first-instance protection. Frames have a four-byte little-endian length and strict UTF-8 JSON. Handshakes and requests are limited to 16 KiB; authenticated replies have a separate 128 KiB bound so a successful status can carry all 128 permitted leases as complete JSON. Successful output is never truncated. Handshake fields are exactly `Version`, `Sid`, `Session`; the server validates the actual client token and returns a random connection nonce. Request fields are exactly `Version`, `RequestId`, `Nonce`, `DeadlineUtcMs`, `Operation`, `Profile`, `Scope`, `LeaseId`, `LeaseUntilMs`. Allowed operations are `status`, `acquire`, `release`, `recover`. Unknown/duplicate fields, paths, commands, non-catalog profiles, skewed versions, expired deadlines and invalid nonces are denied before dispatch.

Client verification binds the kernel-reported pipe server PID to the running SCM service and protected installed image/hash. SCM configuration must specify the fixed executable and LocalSystem account. Where permitted, the process image and SYSTEM token are checked directly. Standard users cannot normally open a SYSTEM process/token; the narrowly checked SCM proof handles access-denied without granting users access to SYSTEM tokens. Service configuration/DACL changes remain restricted to SYSTEM and Administrators.

The same-user attacker is in scope: SID/PID/image checks do not establish human intent. An unelevated process belonging to the same user cannot acquire SYSTEM mutation authority through this protocol. An already elevated Administrator or compromised SYSTEM is outside this privilege boundary. The broker accepts no caller-selected executable, script, path, registry key, endpoint or hostname. Provider and scope arguments are resolved from the embedded catalog and passed using `ProcessStartInfo.ArgumentList` to the fixed system PowerShell with system-only module lookup.

Mutation request receipts persist intent before dispatch and completion afterward. An identical retry with a fresh authenticated connection returns the same receipt; reusing the ID for another payload/account/session is denied. Interrupted dispatch returns uncertainty without blindly repeating mutation. Receipt capacity is bounded; exhaustion refuses new dispatch instead of deleting evidence. All native mutations use the Engine's protected `operation.lock`, including service-free Assisted operations.

The durable receipt limit is 4096. There is no supported automatic pruning or reset: do not delete receipts, the journal or the retained recovery image to bypass refusal. Before capacity is reached, stop admitting new lease operations, release existing leases using their original owner and confirm recovery through the installed candidate. An administrator must retain a protected backup of receipts and journal and arrange a reviewed migration before further dispatch. This release provides no in-place capacity migration command; keep the optional broker disabled when that operational limit is unsuitable. The normal GUI uses Native Assisted without this service.

Installation deliberately rejects a loose assembly build with `INSTALL_REQUIRES_PUBLISHED_SINGLE_FILE`. The IL3000 warning on `Assembly.Location` documents that guard: the empty location of a bundled assembly is required, followed by independent running-image, protected-path, hash and SCM checks. Replacing it with the application directory would remove the guard and is not an equivalent fix.

Journal schema 2 reads schema 1 conservatively and rejects unsupported versions. Lease version 1 contains finite owner references. Each journal write flushes complete temporary bytes to disk before atomic replacement and retains the previous journal. A failed write preserves the last complete recovery authority. NRPT recovery requires owner marker and fingerprint. A present DoH registration marked `Pending` remains ambiguous even when its tuple matches: intent does not prove ownership. Externally edited resources are retained with a safe refusal and the journal remains available. No journal is discarded to manufacture a successful restore.

Offline tests and a build are not native acceptance. Service installation, logon, SCM recovery and real Windows policy changes require a separate isolated Windows laboratory run; no release readiness is implied by this document. See [release preparation](../docs/RELEASING.md).
