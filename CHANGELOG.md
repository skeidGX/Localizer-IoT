# Changelog

All notable changes to Localizer-IoT are documented here.

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
