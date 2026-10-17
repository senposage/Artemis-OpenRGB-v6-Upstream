# OpenRGB v6 for upstream Artemis

This is a separate, experimental plugin project built against the published
`ArtemisRGB.UI.Shared` 1.2025.1223.3 SDK. It does not require the modified
Artemis device-ownership, missing-device, or reconnection APIs. Do not install
it alongside the existing OpenRGB plugin: both use the same plugin GUID.

The two variants are named **OpenRGB Devices v6 (Upstream Artemis)** (this
project; minimum Artemis version `1.2025.1223.3`) and **OpenRGB Devices v6
(Hotplug Fork)** (the existing plugin; minimum customized Artemis version
`1.2026.0920.0232`). The minimum-version field is a compatibility gate, not a
claim that every later upstream release has been runtime-tested.

The plugin retains OpenRGB SDK v6 support, detection-start/end gating,
post-detection settling, reconnect retries, zone/segment devices, and per-SDK
device Direct-mode requests. Direct mode is requested only when a loaded,
unsuspended profile has a bound layer on an enabled Artemis device, and never
as a server-wide command. `Profile.GetAllLayers()` is a public SDK method, but
it merely flattens the tree; suspended profiles, folders and layers are filtered
by this plugin. On older SDKs without `Profile.LedBindingsChanged`, a lightweight
poll notices newly bound devices.

Stock Artemis enumerates devices only when a provider is registered. When the
OpenRGB controller list has settled and the low-level device objects differ
from what Artemis holds, this plugin removes and re-adds its provider through
the public `IDeviceService` API. That permits hotplug discovery but does **not**
preserve Artemis device object identity during reload. Stock Artemis may restore
saved layouts and bindings if its generated device identifier and LED IDs remain
the same; this is not the fork's in-place missing/reconnect behavior.

For split zones, stable zone/segment indices are appended to `DeviceName` so
stock Artemis generates distinct identifiers for siblings, while `Model` is
left unchanged for layout lookup. Two simultaneously attached devices with the
same reported name/model/type remain ambiguous to stock Artemis. This plugin
does not infer hardware identity from VID/PID or silently merge them.

Build from this project with:

```powershell
dotnet build src/Artemis.Plugins.Devices.OpenRGB/Artemis.Plugins.Devices.OpenRGB.csproj -c Release
dotnet test src/Artemis.Plugins.Devices.OpenRGB/OpenRGB.NET.Tests/OpenRGB.NET.Tests.csproj -c Release
```

This project is isolated from `G:\Projects\Artemis.Plugins`; no plugin or
Artemis installation is replaced by a build here.
