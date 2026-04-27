# Code Signing Plan

Deferred from v2.16.3. Capture so future-you doesn't re-derive it.

## Problem

Vanguard (and Defender, AV in general) intermittently block Velopack updates
mid-download. Symptom: the in-app update banner shows "Update failed — try
again later"; `velopack.log` shows successful HTTP 200 download immediately
followed by `FileNotFoundException` on the same `.nupkg` file. Vanguard is
quarantining the package between the download finishing and Velopack opening
it.

Why our build trips Vanguard's heuristics:

1. **`.nupkg` is a renamed zip** containing `LoLReview.App.exe`, `Update.exe`,
   and ~100 DLLs. Vanguard scans inside zips for executables and flags
   "extracting unsigned code into a user-writable path."
2. **Binaries are unsigned.** No Authenticode signature, so the OS treats
   them as the same fingerprint as a homemade tool / cheat injector.
3. **Writes to `%LOCALAPPDATA%`** — same tree historically used by overlays,
   macros, auto-clickers.
4. **Velopack swaps the running exe** mid-update — extracts a new
   `LoLReview.App.exe` and renames it over the old one. From Vanguard's POV
   that pattern matches process injection.
5. **Reads from the LCU API.** Legit dev API, but combined with the above,
   Vanguard's heuristics escalate.

Other unsigned auto-updating League-adjacent apps hit the same wall (OP.GG
Desktop pre-signing, Ascent, custom overlays). The proper fix is
code-signing the binaries so the cert chain validates and the OS treats them
as known publisher.

## Options

### A. Azure Trusted Signing (recommended)

- **Cost**: ~$10/month
- **Validation**: 1-3 days (Microsoft verifies your identity / business name)
- **Hardware**: none (cloud-hosted private key)
- **CI integration**: official `Azure/trusted-signing-action` GitHub Action,
  drop-in step after Velopack pack
- **Cert visibility**: signed binaries show `Microsoft ID Verified CS EOC CA 01`
  as the issuer; the `Signed by` field shows your verified display name
  (e.g. "Sami Fawcett")
- **Renewal**: automatic per-signature; no annual cert refresh dance
- **Catch**: identity validation requires a real ID upload (driver's license
  or passport scan via Microsoft Entra ID)

### B. Sectigo / DigiCert OV cert (traditional)

- **Cost**: ~$200-400/year
- **Validation**: 3-7 days (CA verifies business entity)
- **Hardware**: post-CA/B-Forum 2023 baseline change, the private key MUST
  live on a USB hardware token (YubiKey FIPS, eToken). This is the CI killer
  — the runner can't physically plug into your USB token.
- **CI workaround**: KeyLocker (DigiCert hosted HSM, ~$300/year extra) or
  hosting a self-managed signing server. Expensive + brittle.
- **Cert visibility**: `Sectigo Public Code Signing CA R36`-style chain.
- **Why I'd skip**: cost + USB-token-in-CI mess outweighs the marginal trust
  uplift over Azure Trusted Signing.

### C. Self-signed cert (don't)

- Free, instant.
- Vanguard / Defender / SmartScreen still flag it because the cert chain
  doesn't terminate at a trusted root. Solves nothing.

### D. Document Vanguard exclusions, ship unsigned (status quo)

- Each user adds `%LOCALAPPDATA%\LoLReview\` to Defender exclusions
  (Windows Security → Virus & threat protection → Exclusions → add folder).
- Vanguard piggybacks on a lot of Defender's signal so this catches most of
  it.
- Some users still hit transient quarantine on first install — manual
  Setup.exe from GitHub Releases is the workaround.
- Friends-of-Sami scale only. Doesn't generalize.

## Decision

**Defer signing.** Stay on D for now.

Friends can install via the manual Setup.exe path when in-app updates fail.
Documenting here so when the user count grows past "people I can text",
flipping to A is a 30-minute task.

## When to revisit

- Update-failure rate climbs (count "Update failed" instances in
  `velopack.log` across user reports).
- A non-technical user can't install via the manual fallback.
- Any incident where the failure mode looks like Vanguard quarantine on a
  signed-up beta tester.

## Implementation when we go (Azure path)

### One-time Azure setup (manual, ~30 min + 1-3 day wait)

1. Sign in to [portal.azure.com](https://portal.azure.com) with the same
   Microsoft account that owns the GitHub org / repo.
2. Create a **Trusted Signing Account** resource:
   - Subscription: pay-as-you-go is fine
   - Region: `East US` or `West US 2` (Trusted Signing is region-limited)
   - SKU: `Basic` ($9.99/mo) — plenty of headroom for a few releases per week
3. Create an **Identity Validation** request inside the account:
   - Type: `Individual` (vs. business)
   - Upload government ID (driver's license / passport)
   - Wait 1-3 business days for Microsoft approval
4. Once approved, create a **Certificate Profile** linked to the validated
   identity. This is what signs the binaries.
5. In Azure AD, register an app registration for the GitHub Actions runner:
   - Add **Federated Credential** scoped to the
     `samif0/lol-review` repo, branch `main` (and tag pattern `v*`).
   - Grant the app the `Trusted Signing Certificate Profile Signer` role on
     the Trusted Signing account.

### CI workflow change

Drop into `.github/workflows/release.yml` after the `vpk pack` step but
before `vpk upload`. Pseudocode:

```yaml
- name: Sign binaries with Azure Trusted Signing
  uses: azure/trusted-signing-action@v0.5.1
  with:
    azure-tenant-id: ${{ secrets.AZURE_TENANT_ID }}
    azure-client-id: ${{ secrets.AZURE_CLIENT_ID }}
    azure-client-secret: ${{ secrets.AZURE_CLIENT_SECRET }}
    endpoint: https://eus.codesigning.azure.net/
    trusted-signing-account-name: revu-signing
    certificate-profile-name: revu-public
    files-folder: Releases
    files-folder-filter: exe,dll,msix,nupkg
    file-digest: SHA256
    timestamp-rfc3161: http://timestamp.acs.microsoft.com
    timestamp-digest: SHA256
```

Required GitHub repo secrets:

- `AZURE_TENANT_ID`
- `AZURE_CLIENT_ID`
- `AZURE_CLIENT_SECRET` (or use OIDC federated credentials — preferred,
  removes the long-lived secret)

### Velopack-specific note

Velopack's `vpk pack` builds `Setup.exe` and the `.nupkg` from the published
output dir. The signing step needs to run **after** `vpk pack` so the wrapper
exes are signed too. The `.nupkg` itself doesn't need to be signed (it's
just a zip), but signing the `.exe` and `.dll` files inside the staging
folder, then re-running `vpk pack`, is cleaner than signing the final
artifacts.

Recommended order:

1. `dotnet publish` → `publish/`
2. Sign every `.exe` and `.dll` in `publish/`
3. `vpk pack --packDir publish ...` → `Releases/`
4. Sign `Setup.exe` and `Update.exe` in `Releases/` (these are wrapper exes
   Velopack injected, not from `publish/`)
5. `vpk upload` → GitHub Release

### Verification after first signed release

- Right-click `LoLReview.App.exe` → Properties → Digital Signatures tab
- Should show one signature with the configured display name + countersigned
  by Microsoft Identity Verification CA
- Run `signtool verify /pa LoLReview.App.exe` from VS Developer Prompt for a
  command-line confirmation
- Install fresh on a clean VM with default Defender + Vanguard, confirm no
  quarantine

## References

- Azure Trusted Signing docs:
  https://learn.microsoft.com/azure/trusted-signing/
- GitHub Action: https://github.com/Azure/trusted-signing-action
- Velopack signing guide: https://velopack.io/manual/code-signing/
- CA/B Forum baseline change (why USB tokens are required for traditional
  certs): https://cabforum.org/2022/12/06/ballot-csc-13/
