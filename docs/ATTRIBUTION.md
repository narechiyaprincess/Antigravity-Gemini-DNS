# Attribution and local packaging

Antigravity-Gemini DNS (formerly Antigravity DNS) is distributed under the MIT license in the repository root. The missing local license file was restored from the project's canonical repository, https://github.com/narechiyaprincess/Antigravity-DNS/blob/main/LICENSE, on 2026-10-22.

The native UI uses existing Antigravity-Gemini DNS brand artwork and locally painted service symbols. It does not download artwork while rendering. The Antigravity-Gemini DNS brand is distinct from Google Antigravity. Google, Gemini and browser product names and marks identify the selected services; this application is not endorsed by their owners. Selected browser icons are read from the installed executable and cached for the lifetime of the control; they are not copied into the source package.

The standalone executable contains the Microsoft .NET Windows Desktop runtime, supplied by the installed .NET SDK publishing toolchain. Runtime licensing: https://github.com/dotnet/runtime/blob/main/LICENSE.TXT and https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT. No additional UI framework or third-party NuGet dependency was introduced for this pass.

The source archive includes editable code, embedded PowerShell scripts, the provider catalog, build and verification scripts, the application icon, project documentation and license. It deliberately excludes local state, logs, signing material, build caches and previous executables. Build and verification receipts remain beside the delivered binaries rather than becoming application resources.
