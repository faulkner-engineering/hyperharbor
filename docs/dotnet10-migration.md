# Moving the host to .NET 10 (phase 32)

Status: in progress on branch feat/dotnet10 (started 2026-11-05). Research and a spike on a scratch copy are done
(below); the user's decisions are at the end. The client (Tauri, Rust) does not change.

## Why now
- .NET 8 reaches end of support on 2026-11-10 (dotnet.microsoft.com/platform/support/policy/dotnet-core). A .NET 8 host
  keeps running after that date but gets no more security fixes, and the host serves an mTLS API on the local network.
- .NET 10 is the current LTS: released 2025-11-11, supported until 2028-11-14 (latest patch 10.0.14, SDK 10.0.403).
- The Dependabot ignores for Microsoft.Extensions.* and System.Diagnostics.EventLog majors exist only because the
  host is on .NET 8 (development.md, "Dependency updates"); they go away with this phase.

## What the spike found (2026-11-05, scratch worktree of main at 7b80bac, SDK 10.0.403)
Measured unless marked otherwise. The patch and logs are not in the repository; they were a throwaway.

Retarget:
- TargetFramework net8.0-windows to net10.0-windows in Host.Core, Host.Service, Host.Tray, and Host.Tests;
  Shared.Contracts net8.0 to net10.0. global.json to SDK 10.0.403. CI and release read global.json, so they follow.
- Runtime-versioned packages to 10.0.14 together: Microsoft.Extensions.DependencyInjection.Abstractions,
  Microsoft.Extensions.Logging.Abstractions, System.Diagnostics.EventLog, System.Security.Cryptography.ProtectedData,
  Microsoft.Extensions.Hosting.WindowsServices, and Microsoft.AspNetCore.Mvc.Testing (10.x builds only for net10.0, so
  it cannot move separately). Every other package already has a usable build and stays as it is.
- scripts/e2e.ps1:55 names bin\Debug\net8.0-windows; it is the only hard-coded runtime path.

Build with -warnaserror: 8 errors, each a one-line fix, and nothing suppressed.
- SYSLIB0057 (the X509Certificate2 constructors that take bytes or a path are obsolete in .NET 9):
  Host.Core/Security/HostCertificateStore.cs:43 becomes X509CertificateLoader.LoadPkcs12(pfx, null, DefaultKeySet);
  five test files (RealTlsTests, LiveHostHarness, SecretLeakTests, TestHost, SecurityStoreTests) move to
  X509CertificateLoader the same way.
- WFDEV004 (Form.Closed is obsolete in .NET 10): Host.Tray/Ambient/AmbientScreensaverApp.cs:156 uses FormClosed.
- WFO1000 (a Form property without a designer serialization setting): Host.Tray/Window/WebWindow.cs:54 HideOnClose
  gets [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)].
- No new analyzer or NuGet audit findings with AnalysisLevel latest and NuGetAuditMode all.

Tests: all host tests pass (2863 passed, 42 skipped, 0 failed), and the host end-to-end tests pass (7 of 7). The Rust
loopback test and the cross-version tests were not run in the spike.

Packaging breaks: the .NET 10 Web SDK publishes HyperHarbor.Host.staticwebassets.endpoints.json (53 bytes, empty)
beside the executable, and package.ps1:134-137 rightly refuses a publish with more than one file.

Size and startup (package.ps1 -Fast arguments; startup timed from process start to the API accepting a TCP
connection, in the host's own --self-test mode on a copy of seeded data; one warm-up, then 5 alternating runs each):

| | .NET 8 | .NET 10 |
|---|---|---|
| HyperHarbor.Host.exe | 253,996,240 bytes | 209,825,713 bytes (-17.4%) |
| Startup, median (range) | 883 ms (867 to 934) | 970 ms (946 to 991) |

Why the executable is 44 MB smaller, and why startup is about 90 ms slower, is not yet known.

## Behavior changes to handle (from the .NET 9 and 10 breaking-change lists, checked against the code)
- The host certificate. LoadPkcs12 applies import limits the old constructor did not. host.pfx (DPAPI-protected,
  written by HostCertificateStore.Create with X509Certificate2.Export) is the host's identity that every paired client
  pins, so a host that cannot load it would need every client to pair again. Two protections exist already: a failed
  load throws rather than creating a new certificate, and the update self-test (SelfTestRun) loads the copied
  certificate and completes a TLS handshake with it, so an update that cannot read it is rejected and the old version
  stays (from the code; to be confirmed live). Added in this phase: a test that loads a host.pfx written by the .NET 8
  host (a test-only fixture) with the .NET 10 loader and serves it over TLS.
- BackgroundService.ExecuteAsync now runs entirely on a background thread, including the code before its first await.
  Three services do synchronous work there (Tray/TrayPipeServer.cs:70-72, KeepAwakeService.cs:35-37,
  VmInventoryStartupLogger.cs:23). Only startup and log order change; move that work to StartAsync if a test or the
  tray depends on the order.
- IExceptionHandler: when TryHandleAsync returns true, the middleware no longer records exception diagnostics.
  ApiExceptionHandler logs its own errors, so only the error.type metric tag is lost.
- Single-file native library search: .NET 10 looks in the application directory only when the search paths include
  it. The host's own imports are all system DLLs; WebView2Loader and the Microsoft.Management.Infrastructure natives
  ship inside the bundle, so the packaged build has to be run live (the tray window and the Hyper-V calls).

Checked and not applicable (no code uses them): BinaryFormatter, ServicePointManager, the SslStream cipher
properties, SystemEvents.EventsThreadShutdown, the Rfc2898DeriveBytes constructors (already Pbkdf2), X509 key
parameter changes, HttpClientFactory changes (no AddHttpClient), System.Text.Json metadata changes, WebHostBuilder,
WithOpenApi, cookie redirects, and the WinForms types removed in .NET 9 and 10.

## Steps
Each step is a commit with its tests; the branch is merged when every step is done and the live checks pass.
1. Retarget: TargetFramework, global.json, the six runtime-versioned packages, and e2e.ps1:55, in one commit (the
   packages and the framework cannot move separately).
2. The 8 source fixes, each with the test that covers it (RealTlsTests for the certificate path, the tray tests for
   the form changes).
3. Packaging: set StaticWebAssetsEnabled to false in Host.Service.csproj (the host serves no static web assets; the
   tray UI is embedded resources), so the publish is one file again. Keep package.ps1's one-file check.
4. The host.pfx compatibility test (above), with the fixture written by the 0.2.6 host.
5. The background service order: run the tray and the keep-awake tests with the new threading; move work to StartAsync
   only where a test shows it matters.
6. Size and startup: find what makes the executable smaller (compare the bundle manifests of the two publishes) and
   what costs the 90 ms (ReadyToRun state of the framework assemblies in the bundle, tiered compilation). Decide with
   the user whether the startup cost is acceptable or worth an option such as ReadyToRun for HyperHarbor's own
   assemblies.
7. Documentation: CLAUDE.md and architecture.md (".NET 8"), platform-notes.md:42, development.md (remove the two
   .NET 8 ignore entries), .github/dependabot.yml (remove the Microsoft.Extensions.* and System.Diagnostics.EventLog
   ignores), history.md entry 32, and CHANGELOG.md.
8. Verification: scripts\test-all.ps1 without -SkipE2E (including the cross-version tests, which now pair a .NET 8
   release with the .NET 10 host), scripts\package.ps1 (full, with compression), and live on the development host:
   install as a service and run under the service account (single-file extraction), the tray window and its pages
   (WebView2), the Hyper-V live tests, and a self-update from the installed 0.2.6 to the .NET 10 build and back
   (rollback).

Estimated size: steps 1 to 5 and 7 are about a day; step 6 depends on what it finds; step 8 is half a day of live runs.

## Decisions (with the user, 2026-11-05)
- Version: this ships in 0.3.0, which may also carry other changes. Nothing is released yet, so the version is not
  bumped in this phase; changelog entries go under [Unreleased] and move under 0.3.0 when the user bumps it.
- Timing: start now.
- Startup: step 6 recovers the measured cost (about 90 ms, 10%); accepting it as measured was declined.

Ordering note: X509CertificateLoader does not exist on .NET 8, so steps 1 and 2 are two commits: first the two tray
fixes that also build on .NET 8 (FormClosed, the designer serialization attribute), then the retarget together with
the certificate loader changes.
