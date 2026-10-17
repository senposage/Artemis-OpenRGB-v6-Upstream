# OpenRGB v6 plugin for upstream Artemis

An experimental OpenRGB SDK v6 device provider built against the published
Artemis SDK, without the custom device-ownership and reconnection changes in
our Artemis fork. Its plugin name is **OpenRGB Devices v6 (Upstream Artemis)**.
The other variant is **OpenRGB Devices v6 (Hotplug Fork)** and targets our
customized Artemis build.

See [plugin documentation](src/Artemis.Plugins.Devices.OpenRGB/README.md) for
features, compatibility limits, build instructions, and test status.

The plugin shares its GUID with the original OpenRGB provider and is intended
as an alternative, not a side-by-side installation. Build artifacts are kept
local and are not committed to this repository.
