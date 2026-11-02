# TM170 O&O ShutUp10 integration findings

Owner: `/root/network_service_audit_120`. Research performed 2026-11-02.
Scope: current official capabilities and safe integration constraints. No O&O executable was launched, no host privacy settings were changed, and no Mode/apply/restart test was run.

## Result

The current official Free Edition supports a graphical **Apply only recommended settings** action and exact per-session undo. A safe, fully silent end-to-end workflow has **not been established** from current published vendor documentation: applying a saved settings file from the command line is documented, but automatic export, an automatic current Recommended-only profile generator, and exact undo from the command line are not documented there.

Do not implement invented `/recommended`, `/export`, `/undo`, `/force`, or similar flags. Do not label an old community `.cfg` as the current vendor Recommended set. The optional external-tool adapter added below opens only the vendor's interface and does not expose an apply/import/undo method.

## Official evidence

- [Current release notes](https://www.oo-software.com/en/shutup10/update): version 3.6.1135, dated September 29, 2026. The page confirms historic `/quiet` import and `/nosrp` behavior in the 1.3.1358 notes from 2016. That old entry does not establish the full current CLI syntax, supported exits, auto-export, dynamic recommendations, or undo contract. The vendor download link resolves to `https://dl5.oo-software.com/files/ooshutup10/OOSU10.exe`; its binary was not fetched or run here.
- [First steps](https://manuals.oo-software.com/ooshutup10/en/docs/first-steps/): Free Edition offers Actions → Create a system restoration point, followed by Actions → Apply only recommended settings and confirmation. It distinguishes Recommended, Limited, and Not Recommended, and requires elevation for machine-wide settings.
- [Profiles and export](https://manuals.oo-software.com/ooshutup10/en/docs/common-features/profiles-and-export/): File → Export settings saves Free Edition `.cfg`; File → Import settings applies it. Current documentation explicitly supports applying a settings configuration from the command line. Premium `.pcfg` profiles use a separate workflow and cannot be substituted for `.cfg` input.
- [Profile file structure](https://manuals.oo-software.com/ooshutup10/en/docs/common-features/profile-structure/): `.cfg` records setting IDs and on/off states. A saved off state does not record the underlying pre-change value. Therefore importing the original toggle-state export cannot be advertised as exact registry rollback. Premium Add-Only and Replace-All modes also do not restore original values.
- [Undo history](https://manuals.oo-software.com/ooshutup10/en/docs/common-features/undo-history/): changes in one O&O run are grouped as one session. Each touched setting retains its previous value, including absence. Actions → Undo can restore the last session; history retains ten sessions. Rolling back an older entry also undoes newer entries. Initial settings and Windows factory settings are different operations and must not replace restoration of the immediately preceding session.
- [Free Edition](https://manuals.oo-software.com/ooshutup10/en/docs/introduction/free-edition/): the app is portable but persists preferences and recorded settings in configuration beside the executable when one exists, otherwise LocalAppData. Retain these data and logs; deleting only the EXE is not a rollback strategy.
- [Vendor support and EULA](https://www.oo-software.com/en/shutup10/support), section 2.2: electronic redistribution requires exact, complete, unmodified distribution with its documentation/notices. It explicitly treats packaging with an installer or wrapper as an unauthorized modification. Do not embed the O&O executable in TMODE or add it to TMODE release ZIPs. A vendor link or separate user download avoids claiming redistribution permission that has not been granted. This clause concerns redistributing binaries; it does not establish a prohibition on invoking a separately obtained, unmodified tool.

## Supported next path

1. Root verifies the requested restore point before any apply, with the actual success result retained.
2. Obtain the Free Edition separately from the official vendor. Verify its Authenticode chain, publisher identity and actual version before launch; do not trust a filename or download URL as executable identity.
3. Retain the existing O&O settings/history data and export the current `.cfg` through its documented UI as an additional toggle-state reference.
4. Use the current app's observed **Apply only recommended settings** action. This uses current per-device availability and vendor recommendation levels. Never use Apply all settings, Limited settings, AI removal, or factory reset to stand in for it.
5. Record the resulting O&O session and actual application errors. Root must verify the session is available in Undo history before claiming targeted recovery readiness. A restore point remains the broader safety net, not evidence that each change was applied or exactly recovered.
6. If the product requirement remains fully silent baseline/apply/undo, obtain a supported current vendor automation contract or an explicitly reviewed, version-pinned profile with exact independent state capture. Until then expose the precise unsupported limitation; do not silently apply a stale file.

## Integration implications

- This is a persistent optional privacy configuration, not a temporary Tourney Mode on/off toggle.
- No measured FPS, frame-time or end-to-end latency gain follows from these vendor privacy descriptions. Describe any gaming benefit as unmeasured until a controlled benchmark demonstrates it.
- The Free Edition does not need a permanent service. Premium automatic enforcement is paid and adds a background service; neither purchase nor deployment is part of this research.
- `OOSPrivacy.cs` offers read-only inspection and a parameterless external GUI launch. It verifies Windows Authenticode trust/revocation, reads the selected signer from WinVerifyTrust state, checks exact O&O publisher/product identity and pins version 3.6.1135.0. It holds a read-only, no-write/no-delete-sharing handle through launch and rejects network, alternate-stream, ambiguous and reparse paths. Both the actual game/journal gate and mandatory caller gate are repeated immediately before launch. Root must add engine and restore-point checks in the caller gate and invoke potentially network-bound trust checking away from its UI thread. No O&O process was launched to validate its metadata; unexpected legitimate metadata remains blocked pending review.
- `OOSPrivacyTests.cs` provides five inert/mock test groups. Cfg inspection accepts only supported text-line structure and explicitly returns false for claims of current Recommended-only selection and exact recovery. The tests never run a real executable. No shared build/test suite was run by this child; root owns execution and final integration.
- The selected-signer native layout and trust APIs were checked against [WinVerifyTrust](https://learn.microsoft.com/en-us/windows/win32/api/wintrust/nf-wintrust-winverifytrust), [CRYPT_PROVIDER_SGNR](https://learn.microsoft.com/en-us/windows/win32/api/wintrust/ns-wintrust-crypt_provider_sgnr), [CRYPT_PROVIDER_CERT](https://learn.microsoft.com/en-us/windows/win32/api/wintrust/ns-wintrust-crypt_provider_cert), and [CertGetNameStringW](https://learn.microsoft.com/en-us/windows/win32/api/wincrypt/nf-wincrypt-certgetnamestringw).

## Updater handoff retained

`AppUpdaterTests.cs` is ready for root integration using `await AppUpdaterTests.RunAsync(Test)`. It contains 16 mock-HTTPS/inert-file groups. This child has not run a shared build or test suite. Root owns all actual compilation, tests, UI integration, publishing and release checks.
