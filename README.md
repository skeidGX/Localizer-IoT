# Localizer-IoT — Windows IoT Enterprise LTSC Russian Localizer

A WinForms utility for inspecting a Windows installation ISO and servicing a selected Windows image with **locally supplied, compatible** language CAB/MSU packages.

## Build status

The project is being built on a Windows GitHub Actions runner as a self-contained `win-x64` single-file executable. See [Actions](https://github.com/skeidGX/Localizer-IoT/actions) for the live build and the downloadable workflow artifact.

## Current implementation

- GUI with ISO file picker and drag-and-drop support.
- Reads `install.wim` / `install.esd` image indexes using Windows DISM.
- Stages the ISO into a separate working folder; the selected source ISO is never used as the output path.
- Applies supplied CAB/MSU packages to the selected Windows image and requests `ru-RU` system/user locale and Russian input.
- Writes a separate bootable ISO using `oscdimg.exe` and writes a report alongside it.
- Logs progress and servicing output.

## Important limitations of this initial build

- **Automatic language-pack discovery/download is not implemented yet.** The user must provide packages that exactly match the target release, build, architecture, and servicing level.
- This version services only the selected `install.wim` image. It does **not** yet service `boot.wim` / Windows Setup, WinPE, WinRE, OOBE resources, or every edition index.
- `oscdimg.exe` from Windows ADK Deployment Tools must already be installed. DISM is supplied by Windows.
- A successful compilation is not proof that a produced ISO boots or that all UI components are Russian. Test output in a virtual machine before deployment.

## Requirements

- Windows 10/11 x64.
- Administrator rights for image servicing.
- Compatible language CAB/MSU packages.
- Windows ADK Deployment Tools (`oscdimg.exe`) for ISO creation.

## Safety

The tool refuses to use the same path for source and output. Keep a backup of the source ISO and test the output before deploying it to physical hardware.
