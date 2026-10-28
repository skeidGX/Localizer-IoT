# Changelog

All notable changes to Localizer-IoT are documented here.

## [0.9.0.0] — 2026-10-29

### Added
- Added JSON save/load profiles for source paths, explicit CAB list, output path, selected image indexes, and the opt-in language-capability Windows Update setting.
- Added offline ZIP export for matching Russian Language Pack/FOD/WinPE CAB files and Setup `sources\\ru-ru` resources already present in selected extracted folders. The archive includes a per-file SHA-256 manifest and excludes likely OS update packages.
- Changed WinRE servicing to require recovery language resources from a suitable language/optional-features source; it now pairs WinRE `lp.cab` and language CABs only with optional components present in the recovery image and does not substitute the installed Windows language pack or ADK media.
- Added a skip for language package identities already reported as installed by DISM, avoiding redundant `/Add-Package` operations across repeated servicing.
- Added local SHA-256 cache manifests for DISM-readable CAB payloads and fallback to a matching verified cache if online UUP discovery/download cannot provide a usable package.
- Added up to three parallel UUP payload transfers, retry/backoff for temporary network/API failures, HTTP Range continuation for partial payloads, and refreshed payload URLs after expiry/authorization errors.
- Added a visible cancellation action for UUP discovery/download; verified cache entries are reused and incomplete `.partial` downloads are retained for possible resumption.
- Added an explicit, disabled-by-default option to use the configured Windows Update/WSUS source only for missing language Features on Demand capabilities. This option does not add LCU, SSU, KB, MSU, or other OS update packages.
- Expanded the output report to state whether Windows Update fallback for language capabilities was enabled and to confirm that OS update payloads were not integrated.

### Fixed
- Fixed the automatic Language Pack discovery condition: selecting a Russian donor ISO no longer suppresses UUP discovery when no standalone core Language Pack CAB is available. An extracted donor folder containing a standalone core language CAB can still satisfy the package source without an unnecessary automatic download.
- Improved retry behavior for UUP Dump API queries and limited parallel download concurrency to reduce transient failures and excessive connection pressure.

### Compatibility and safety
- The default remains offline servicing: local package/FOD sources are tried first and Windows Update is not contacted unless the user explicitly enables the language-capability-only option.
- The downloader continues to exclude likely Windows update payloads and validates downloaded files against UUP Dump's published size and SHA-1 metadata. SHA-1 here is an integrity check against the API metadata, not an independent publisher signature.
- This release is still alpha. The exact user ISO, image servicing, setup boot, and WinRE have not been end-to-end tested in a VM in this environment; review the report and test the produced ISO before deployment.

## [0.1.0.1] — 2026-10-29

### Added
- Added a dedicated optional Russian ISO-donor/media field supporting an ISO or extracted Windows media folder.
- Added a "Проверить донор" diagnostic that reads the donor image's first-index version/architecture, checks for `sources\\ru-ru`, and counts separate ru-RU Language Pack, language FOD, and WinPE CAB candidates.
- Added donor media to language-package/FOD/Setup-resource discovery while preserving the donor ISO and the original target ISO unchanged.
- Extended the WinPE preflight scan to accept donor ISO files as sources, not only already-extracted folders.
- Set the application file/product version to Alpha 0.1.0.1 and updated the release workflow to publish four-part version tags such as `v0.1.0.1`.

### Compatibility and safety
- Donor Setup resources are copied only when the servicing baseline and architecture pass the existing compatibility checks; language CAB packages are still validated by DISM before application.
- The program does not synthesize a Language Pack CAB from an already-localized `install.wim` or the installed component store. If the donor media has no independent core Language Pack CAB, a compatible official Language Pack source is still required.
- UUP Dump remains an optional best-effort source. The donor ISO may contain Setup resources without containing the standalone Language Pack or every FOD/WinPE package.
- The executable build pipeline has passed, but end-to-end servicing of the exact user ISO and VM boot testing have not yet been completed. This remains an alpha preview.


## [0.1.0-alpha.2] — 2026-10-28

### Fixed
- Made UUP build parsing tolerant of both abbreviated build strings (for example `26100.9711`) and full OS version strings (for example `10.0.26100.9711`).
- Added fallback candidate records that are validated against the live UUP Dump API for Windows 11 24H2 / IoT Enterprise LTSC 2024 x64 (base build 26100) when UUP Dump search returns no matching rows.
- Reordered candidate selection to prefer the shared `.1` language-package baseline, then newer candidates, while continuing to exclude cumulative OS update payloads.
- Updated the release workflow to accept versioned tags from commit messages, so subsequent preview releases can be published without overwriting the first tag.

### Notes
- Fallback UUP entries are only discovery seeds: the app still checks language availability and fetches file metadata live from UUP Dump before downloading.
- The release workflow now derives its tag from the first line of a `release: vMAJOR.MINOR.PATCH[-prerelease]` commit title.
- This alpha remains best-effort. The new fallback was not verified end-to-end against the user's ISO or a VM boot test.

## [0.1.0-alpha.1] — 2026-10-28

### Added
- Russian-language WinForms GUI for selecting a Windows ISO, choosing output location, and processing one or all image indexes.
- Automatic detection of image edition, OS build, and architecture through DISM.
- Automatic best-effort discovery and download of Russian Language Pack files through the UUP Dump JSON API.
- SHA-1 and size verification against the API metadata for downloaded language payloads.
- Conversion of language ESD packages to CAB using a pinned helper-tool revision whose Git blob hashes are verified.
- Compatibility handling for shared language-package baselines, including Windows 10 19041–19045 and Windows 11 22621/22631 families.
- Best-effort integration of Russian language capabilities (Basic, Fonts, OCR, Speech, Text-to-Speech, and Handwriting) when compatible FOD packages are present.
- Automatic checks for required WinPE language components; optional download and installation of compatible Microsoft ADK/WinPE add-on installers for selected known Windows release families, with Authenticode signature checks.
- Russian locale defaults, English fallback, Russian and English keyboard layouts, and Russian time-zone configuration.
- Report and progress logging; the original ISO is copied to a separate working directory and is not modified.

### Safety and compatibility
- Windows update payloads resembling KB, LCU, SSU, cumulative update, or MSU packages are excluded to avoid servicing Windows with updates.
- Language packages are checked for available version/build and architecture metadata before DISM servicing.
- The installer warns when required Setup/WinPE or language capability resources are not found or cannot be verified.

### Known limitations
- This is an **alpha preview**. The EXE compile and packaging pipeline pass, but a complete end-to-end run against a real Windows IoT Enterprise LTSC ISO and boot testing in a VM have not yet been completed.
- Automatic UUP discovery is best-effort. Some older releases or language payloads may not be indexed or downloadable.
- A compatible full Features on Demand repository is not guaranteed for every Windows release; OCR, speech, handwriting, or fonts may be unavailable if their matching packages are missing.
- Full Russian Windows Setup localization may still require matching `sources\\ru-ru` resources from Russian Windows media. Automatic matching ADK/WinPE setup is limited to known servicing baselines; unsupported builds may need manual sources.
- WinRE/Setup behavior can vary by image layout. Review the generated `.report.txt` and test the resulting ISO in a virtual machine before using it on a physical device.
- The original English language is retained as fallback, but complete bilingual behavior has not been validated on every edition.

### Distribution note
- This alpha EXE is not Authenticode code-signed. Windows may display a SmartScreen or unknown-publisher warning.

### Requirements
- Windows 10/11 x64 servicing workstation and administrator rights.
- Internet access for language-package discovery and, if required, ADK/WinPE setup.
- Sufficient free disk space for staging the Windows media and servicing WIM images.
