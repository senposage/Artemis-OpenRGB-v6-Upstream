# OpenRGB Devices v6 (Upstream Artemis) — 2026.1017.1518

Experimental build for unmodified Artemis. It targets the published
`ArtemisRGB.UI.Shared` 1.2025.1223.3 package and does not call fork-only
device identity or reconnection APIs.

- Keeps OpenRGB SDK v6 support, detection settling, connection retry, and
  per-device Direct-mode requests for devices with bound active layers.
- Uses stock Artemis provider remove/add APIs after a settled hotplug change.
- Gives split zones and segments distinct, stable names for stock Artemis IDs.
- Removes the fork-specific persistent-ID and ownership code.

Limits: stock Artemis cannot retain a missing device object or resolve two
indistinguishable same-model devices. Provider reload may briefly interrupt
rendering. Physical hotplug and saved-binding recovery still need testing with
an unmodified Artemis build. Do not install this beside the original OpenRGB
plugin because both use the same plugin GUID.

Validation: Release build succeeded against the published SDK; 5 OpenRGB
protocol/provider tests passed. No physical-device validation yet.
