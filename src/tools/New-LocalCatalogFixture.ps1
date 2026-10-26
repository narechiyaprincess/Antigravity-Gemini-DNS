param(
 [Parameter(Mandatory=$true)][string]$AssemblyPath,
 [Parameter(Mandatory=$true)][string]$OutputDirectory,
 [long]$Sequence=1
)
$ErrorActionPreference='Stop'
# Run with PowerShell 7 on .NET 10. This creates a disposable local-test root only.
if($Sequence -lt 1){throw 'Sequence must be positive'}
if(Test-Path -LiteralPath $OutputDirectory){throw 'OutputDirectory must be new; existing data is preserved'}
Add-Type -Path ([IO.Path]::GetFullPath($AssemblyPath))
$key=[Security.Cryptography.ECDsa]::Create([Security.Cryptography.ECCurve+NamedCurves]::nistP256)
try {
 $now=[DateTimeOffset]::UtcNow
 $settings=[AntigravityGeminiDns.AppSettings]::new();$settings.NoSave=$true
 $pack=[AntigravityGeminiDns.LegacyScenarioAdapter]::Migrate([AntigravityGeminiDns.ProfileCatalog]::BuiltIn,$settings).Pack
 $pack=[AntigravityGeminiDns.PackValidator]::Validate($pack)
 $payload=[Text.Json.JsonSerializer]::Serialize($pack,[AntigravityGeminiDns.ScenarioPack],[AntigravityGeminiDns.PolicyJson]::Options)
 $root=[AntigravityGeminiDns.CatalogRoot]::new('explicit-local-test-key',[Convert]::ToBase64String($key.ExportSubjectPublicKeyInfo()),$now.AddDays(-1),$now.AddDays(30),$false)
 $manifest=[AntigravityGeminiDns.CatalogManifest]::new(1,'local-test',$Sequence,$root.Id,'ECDSA-P256-SHA256-P1363',$now.AddMinutes(-1),$now.AddDays(7),1,1,1,[AntigravityGeminiDns.CatalogVerifier]::Hash($payload))
 $signature=$key.SignData([AntigravityGeminiDns.CatalogVerifier]::SignedBytes($manifest),[Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.DSASignatureFormat]::IeeeP1363FixedFieldConcatenation)
 $document=[AntigravityGeminiDns.SignedCatalogDocument]::new($manifest,$payload,[Convert]::ToBase64String($signature))
 # Use the strict parser after writing, so the fixture exercises the same wire envelope as import.
 New-Item -ItemType Directory -Path $OutputDirectory | Out-Null
 $roots=[ordered]@{Schema=1;Channel='local-test';Roots=@($root)} | ConvertTo-Json -Depth 8
 [IO.File]::WriteAllText((Join-Path $OutputDirectory 'roots.local-test.json'),$roots,[Text.UTF8Encoding]::new($false))
 [IO.File]::WriteAllText((Join-Path $OutputDirectory 'catalog.local-test.json'),[Text.Json.JsonSerializer]::Serialize($document,[AntigravityGeminiDns.SignedCatalogDocument],[AntigravityGeminiDns.PolicyJson]::Options),[Text.UTF8Encoding]::new($false))
 # No private key is written. Regenerate a new fixture directory when testing key rotation.
 $verifier=[AntigravityGeminiDns.CatalogVerifier]::new([AntigravityGeminiDns.CatalogRoot[]]@($root),'local-test',1)
 $null=$verifier.Verify([AntigravityGeminiDns.CatalogStore]::ReadLocal((Join-Path $OutputDirectory 'catalog.local-test.json')),$now)
 Write-Output ('LOCAL_TEST_CATALOG_FIXTURE_PASS '+[IO.Path]::GetFullPath($OutputDirectory))
} finally {$key.Dispose()}
