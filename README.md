# IoT LTSC Russian Localizer

A Windows desktop utility for inspecting Windows IoT Enterprise LTSC ISO media and preparing a Russian-language serviced image.

## Build

Pushes to `main` trigger a Windows GitHub Actions build. The self-contained `win-x64` application is published as a workflow artifact.

## Scope and safety

The application must run elevated for ISO mounting and DISM image servicing. It never modifies the selected source ISO: it works in a separate staging folder and writes a separate output ISO. Language-pack and Features-on-Demand compatibility depends on the exact Windows release, architecture, and servicing level; the app must report unresolved requirements rather than silently using mismatched packages.

**Status:** initial project setup; not yet validated against actual Windows IoT LTSC media.