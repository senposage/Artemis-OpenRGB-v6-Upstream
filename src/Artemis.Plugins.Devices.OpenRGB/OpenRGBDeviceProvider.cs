using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Artemis.Core;
using Artemis.Core.DeviceProviders;
using Artemis.Core.Services;
using Avalonia.Threading;
using RGB.NET.Core;
using RGB.NET.Devices.OpenRGB;
using Serilog;
using RGBDeviceProvider = RGB.NET.Devices.OpenRGB.OpenRGBDeviceProvider;
using Timer = System.Timers.Timer;

namespace Artemis.Plugins.Devices.OpenRGB;

[PluginFeature(Name = "OpenRGB Device Provider")]
public class OpenRGBDeviceProvider : DeviceProvider
{
    private static readonly TimeSpan SnapshotRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ConnectionRetryDelay = TimeSpan.FromSeconds(5);
    private readonly ILogger _logger;
    private readonly IDeviceService _deviceService;
    private readonly IProfileService _profileService;
    private readonly PluginSetting<List<OpenRGBServerDefinition>> _deviceDefinitionsSettings;
    private readonly PluginSetting<bool> _forceAddAllDevicesSetting;
    private readonly PluginSetting<bool> _requestDirectForActiveLayersSetting;
    private readonly Timer _refreshTimer;
    private readonly Timer _reconnectTimer;
    private readonly Timer _bindingPollTimer;
    private readonly EventInfo? _ledBindingsChangedEvent = typeof(Profile).GetEvent("LedBindingsChanged");
    private readonly SemaphoreSlim _reconcileLock = new(1, 1);
    private readonly HashSet<OpenRGBServerDefinition> _detectingServers = [];
    private readonly HashSet<Profile> _observedProfiles = [];
    private HashSet<(string Server, uint ControllerId)> _boundSdkDevices = [];
    private readonly Dictionary<string, string> _sdkStatuses = [];
    private readonly object _stateLock = new();
    private volatile bool _running;

    public OpenRGBDeviceProvider(IDeviceService deviceService, IProfileService profileService, PluginSettings settings, ILogger logger)
    {
        _logger = logger;
        _deviceService = deviceService;
        _profileService = profileService;
        _forceAddAllDevicesSetting = settings.GetSetting("ForceAddAllDevices", false);
        _requestDirectForActiveLayersSetting = settings.GetSetting("RequestDirectForActiveLayers", true);
        _deviceDefinitionsSettings = settings.GetSetting("DeviceDefinitions", new List<OpenRGBServerDefinition>
        {
            new() { ClientName = "Artemis", Ip = "127.0.0.1", Port = 6742 }
        });

        CreateMissingLedsSupported = false;
        RemoveExcessiveLedsSupported = true;
        _refreshTimer = new Timer(500) { AutoReset = false };
        _refreshTimer.Elapsed += OnRefreshTimerElapsed;
        _reconnectTimer = new Timer(1000) { AutoReset = false };
        _reconnectTimer.Elapsed += OnReconnectTimerElapsed;
        // Older Artemis builds do not expose Profile.LedBindingsChanged. Poll only
        // the in-memory bindings on those builds; an actual SDK refresh is queued
        // only when a controller becomes newly bound.
        _bindingPollTimer = new Timer(1000) { AutoReset = true };
        _bindingPollTimer.Elapsed += OnBindingPollTimerElapsed;
    }

    public override RGBDeviceProvider RgbDeviceProvider => RGBDeviceProvider.Instance;

    public string SdkStatus
    {
        get
        {
            lock (_stateLock)
                return _sdkStatuses.Count == 0
                    ? "SDK status: not connected"
                    : "SDK status: " + string.Join("; ", _sdkStatuses.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key} — {pair.Value}"));
        }
    }

    public override void Enable()
    {
        _running = true;
        RGBDeviceProvider provider = RgbDeviceProvider;

        provider.Exception += Provider_OnException;
        provider.DeviceListUpdated += ProviderOnDeviceListUpdated;
        provider.ConnectionLost += ProviderOnConnectionLost;
        provider.DetectionStarted += ProviderOnDetectionStarted;
        provider.DetectionEnded += ProviderOnDetectionEnded;
        _deviceService.DeviceAdded += DeviceServiceOnDeviceAdded;
        _deviceService.LedsChanged += DeviceServiceOnLedsChanged;
        _profileService.ProfileActivated += ProfileServiceOnProfileActivated;
        _profileService.ProfileDeactivated += ProfileServiceOnProfileDeactivated;
        foreach (Profile profile in _profileService.ProfileCategories
                     .SelectMany(category => category.ProfileConfigurations)
                     .Select(configuration => configuration.Profile)
                     .OfType<Profile>())
            ObserveProfile(profile);

        foreach (OpenRGBServerDefinition definition in _deviceDefinitionsSettings.Value)
            provider.DeviceDefinitions.Add(definition);
        provider.ForceAddAllDevices = _forceAddAllDevicesSetting.Value;
        provider.ForceRequestDirectModeOnRefresh = _requestDirectForActiveLayersSetting.Value;
        provider.IsSdkDeviceInUse = IsSdkDeviceInUse;

        try
        {
            _deviceService.AddDeviceProvider(this);
        }
        catch
        {
            _deviceService.DeviceAdded -= DeviceServiceOnDeviceAdded;
            _deviceService.LedsChanged -= DeviceServiceOnLedsChanged;
            _profileService.ProfileActivated -= ProfileServiceOnProfileActivated;
            _profileService.ProfileDeactivated -= ProfileServiceOnProfileDeactivated;
            StopObservingProfiles();
            DetachProviderEvents(provider);
            provider.IsSdkDeviceInUse = null;
            provider.ForceRequestDirectModeOnRefresh = false;
            provider.Dispose();
            _running = false;
            throw;
        }

        UpdateStatuses();
        if (_ledBindingsChangedEvent == null)
        {
            _boundSdkDevices = CaptureBoundSdkDevices();
            _bindingPollTimer.Start();
        }
        if (_requestDirectForActiveLayersSetting.Value)
            QueueReconcile();
        if (provider.DeviceDefinitions.Any(definition => !definition.Connected))
            _reconnectTimer.Start();
    }

    public override void Disable()
    {
        _running = false;
        _refreshTimer.Stop();
        _reconnectTimer.Stop();
        _bindingPollTimer.Stop();
        _deviceService.DeviceAdded -= DeviceServiceOnDeviceAdded;
        _deviceService.LedsChanged -= DeviceServiceOnLedsChanged;
        _profileService.ProfileActivated -= ProfileServiceOnProfileActivated;
        _profileService.ProfileDeactivated -= ProfileServiceOnProfileDeactivated;
        StopObservingProfiles();
        RGBDeviceProvider provider = RgbDeviceProvider;

        try
        {
            _deviceService.RemoveDeviceProvider(this);
        }
        finally
        {
            DetachProviderEvents(provider);
            provider.IsSdkDeviceInUse = null;
            provider.ForceRequestDirectModeOnRefresh = false;
            provider.Dispose();
            lock (_stateLock)
            {
                _detectingServers.Clear();
                _sdkStatuses.Clear();
            }
        }
    }

    private void DetachProviderEvents(RGBDeviceProvider provider)
    {
        provider.Exception -= Provider_OnException;
        provider.DeviceListUpdated -= ProviderOnDeviceListUpdated;
        provider.ConnectionLost -= ProviderOnConnectionLost;
        provider.DetectionStarted -= ProviderOnDetectionStarted;
        provider.DetectionEnded -= ProviderOnDetectionEnded;
    }

    private void Provider_OnException(object sender, ExceptionEventArgs args)
    {
        // Connection attempts and snapshot confirmation are expected while OpenRGB
        // restarts or scans. Logging their stack traces on every retry creates a
        // surprising amount of allocation and disk activity.
        if (args.Exception is TimeoutException ||
            args.Exception.Message == "OpenRGB controller removals are waiting for a confirming snapshot")
        {
            _logger.Debug("OpenRGB reconciliation deferred: {Message}", args.Exception.Message);
            return;
        }

        _logger.Debug(args.Exception, "OpenRGB Exception: {message}", args.Exception.Message);
    }

    private void ProviderOnDetectionStarted(OpenRGBServerDefinition definition)
    {
        lock (_stateLock)
        {
            _detectingServers.Add(definition);
            _sdkStatuses[GetDefinitionKey(definition)] = "device detection in progress";
        }
        // A previously queued refresh/reconnect must not sample OpenRGB while its
        // controller registry is intentionally incomplete.
        _refreshTimer.Stop();
        _reconnectTimer.Stop();
        _logger.Debug("OpenRGB detection started for {Server}; pausing device-list reconciliation", GetDefinitionKey(definition));
    }

    private void ProviderOnDetectionEnded(OpenRGBServerDefinition definition)
    {
        lock (_stateLock)
        {
            _detectingServers.Remove(definition);
            _sdkStatuses[GetDefinitionKey(definition)] = "device detection complete; waiting for device list to settle";
        }
        _logger.Debug("OpenRGB detection ended for {Server}; waiting for the post-detection quiet period", GetDefinitionKey(definition));
        QueueReconcile();
    }

    private void ProviderOnDeviceListUpdated(OpenRGBServerDefinition definition)
    {
        lock (_stateLock)
        {
            if (_detectingServers.Contains(definition))
                return;
        }

        QueueReconcile();
    }

    private void ProviderOnConnectionLost(OpenRGBServerDefinition definition)
    {
        definition.Connected = false;
        lock (_stateLock)
        {
            _detectingServers.Remove(definition);
            _sdkStatuses[GetDefinitionKey(definition)] = "disconnected";
        }
        ScheduleReconnect(SnapshotRetryDelay);
    }

    private void DeviceServiceOnDeviceAdded(object? sender, DeviceEventArgs args)
    {
        if (_requestDirectForActiveLayersSetting.Value && ReferenceEquals(args.Device.DeviceProvider, this))
            QueueReconcile();
    }

    private void DeviceServiceOnLedsChanged(object? sender, EventArgs args)
    {
        // ProfileService repopulates deferred bindings on this same event before our
        // debounced reconciliation reads the OpenRGB controller mode.
        if (_requestDirectForActiveLayersSetting.Value)
            QueueReconcile();
    }

    private void ProfileServiceOnProfileActivated(object? sender, ProfileConfigurationEventArgs args)
    {
        if (args.ProfileConfiguration.Profile is { } profile)
            ObserveProfile(profile);
        if (_requestDirectForActiveLayersSetting.Value)
            QueueReconcile();
    }

    private void ProfileServiceOnProfileDeactivated(object? sender, ProfileConfigurationEventArgs args)
    {
        lock (_stateLock)
        {
            foreach (Profile profile in _observedProfiles.Where(profile => ReferenceEquals(profile.Configuration, args.ProfileConfiguration)).ToList())
            {
                _ledBindingsChangedEvent?.RemoveEventHandler(profile, (EventHandler) ProfileOnLedBindingsChanged);
                _observedProfiles.Remove(profile);
            }
        }
    }

    private void ObserveProfile(Profile profile)
    {
        lock (_stateLock)
        {
            if (_observedProfiles.Add(profile))
                _ledBindingsChangedEvent?.AddEventHandler(profile, (EventHandler) ProfileOnLedBindingsChanged);
        }
    }

    private void StopObservingProfiles()
    {
        lock (_stateLock)
        {
            foreach (Profile profile in _observedProfiles)
                _ledBindingsChangedEvent?.RemoveEventHandler(profile, (EventHandler) ProfileOnLedBindingsChanged);
            _observedProfiles.Clear();
        }
    }

    private void ProfileOnLedBindingsChanged(object? sender, EventArgs args)
    {
        if (_requestDirectForActiveLayersSetting.Value)
            QueueReconcile();
    }

    private void OnBindingPollTimerElapsed(object? sender, ElapsedEventArgs args)
    {
        if (!_running || !_requestDirectForActiveLayersSetting.Value)
            return;

        try
        {
            HashSet<(string Server, uint ControllerId)> current = CaptureBoundSdkDevices();
            bool newlyBound = current.Except(_boundSdkDevices).Any();
            _boundSdkDevices = current;
            if (newlyBound)
                QueueReconcile();
        }
        catch (InvalidOperationException)
        {
            // A profile can change while this compatibility-only snapshot is read.
            // The next poll will observe the completed selection.
        }
    }

    private HashSet<(string Server, uint ControllerId)> CaptureBoundSdkDevices()
    {
        Dictionary<ArtemisDevice, (string Server, uint ControllerId)> available = _deviceService.Devices
            .Where(device => device.IsEnabled && device.RgbDevice.DeviceInfo is OpenRGBDeviceInfo)
            .ToDictionary(device => device, device =>
            {
                OpenRGBDeviceInfo info = (OpenRGBDeviceInfo) device.RgbDevice.DeviceInfo;
                return (info.ServerIdentity, info.ControllerId);
            });

        return _profileService.ProfileCategories
            .SelectMany(category => category.ProfileConfigurations)
            .Where(configuration => !configuration.IsSuspended && !configuration.IsMissingModule)
            .Select(configuration => configuration.Profile)
            .OfType<Profile>()
            .SelectMany(profile => profile.GetAllLayers())
            .Where(IsLayerAvailable)
            .SelectMany(layer => layer.Leds)
            .Where(led => available.ContainsKey(led.Device))
            .Select(led => available[led.Device])
            .ToHashSet();
    }

    private bool IsSdkDeviceInUse(OpenRGBServerDefinition definition, uint sdkDeviceId)
    {
        string serverIdentity = GetDefinitionKey(definition);
        HashSet<ArtemisDevice> devices = _deviceService.Devices
            .Where(device => device.IsEnabled &&
                             device.RgbDevice.DeviceInfo is OpenRGBDeviceInfo info &&
                             info.ServerIdentity == serverIdentity && info.ControllerId == sdkDeviceId)
            .ToHashSet();
        if (devices.Count == 0)
            return false;

        return _profileService.ProfileCategories
            .SelectMany(category => category.ProfileConfigurations)
            .Where(configuration => !configuration.IsSuspended && !configuration.IsMissingModule)
            .Select(configuration => configuration.Profile)
            .Where(profile => profile != null)
            .Any(profile => profile!.GetAllLayers().Any(layer =>
                IsLayerAvailable(layer) && layer.Leds.Any(led => devices.Contains(led.Device))));
    }

    private static bool IsLayerAvailable(Layer layer)
    {
        for (ProfileElement element = layer; element != null; element = element.Parent)
        {
            if (element.Suspended)
                return false;
        }

        return true;
    }

    private void QueueReconcile()
    {
        if (!_running)
            return;

        // OpenRGB emits several list changes while a detection pass is settling. Wait
        // for a short quiet period so Artemis never sees an intermediate controller list.
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    private async void OnRefreshTimerElapsed(object sender, ElapsedEventArgs e) => await ReconcileDeviceList();

    private async Task ReconcileDeviceList()
    {
        await _reconcileLock.WaitAsync();
        try
        {
            if (!_running)
                return;

            lock (_stateLock)
            {
                if (_detectingServers.Count > 0)
                    return;
            }

            bool refreshed = RgbDeviceProvider.TryRefreshDevices();
            if (!refreshed || RgbDeviceProvider.HasPendingRemovals)
                ScheduleReconnect(GetReconnectDelay());
            else if (!HostSnapshotMatchesProvider())
                await ReloadHostProviderAsync();
            UpdateStatuses();
        }
        finally
        {
            _reconcileLock.Release();
        }
    }

    private async void OnReconnectTimerElapsed(object sender, ElapsedEventArgs e)
    {
        await _reconcileLock.WaitAsync();
        try
        {
            if (!_running)
                return;

            lock (_stateLock)
            {
                if (_detectingServers.Count > 0)
                {
                    ScheduleReconnect(SnapshotRetryDelay);
                    return;
                }
            }

            bool repaired = RgbDeviceProvider.DeviceDefinitions.Any(definition => !definition.Connected)
                ? RgbDeviceProvider.TryReconnectDevices()
                : RgbDeviceProvider.TryRefreshDevices();
            if (!repaired)
            {
                ScheduleReconnect(GetReconnectDelay());
                return;
            }

            if (RgbDeviceProvider.HasPendingRemovals)
                ScheduleReconnect(SnapshotRetryDelay);
            else if (!HostSnapshotMatchesProvider())
                await ReloadHostProviderAsync();

            UpdateStatuses();
            if (_requestDirectForActiveLayersSetting.Value)
                QueueReconcile();
        }
        finally
        {
            _reconcileLock.Release();
        }
    }

    private void UpdateStatuses()
    {
        lock (_stateLock)
        {
            foreach (OpenRGBServerDefinition definition in RgbDeviceProvider.DeviceDefinitions)
                _sdkStatuses[GetDefinitionKey(definition)] = definition.Connected
                    ? $"protocol v{RgbDeviceProvider.ProtocolVersionFor(definition)}, connected"
                    : $"disconnected{(string.IsNullOrWhiteSpace(definition.LastError) ? string.Empty : $": {definition.LastError}")}";
        }
    }

    private bool HostSnapshotMatchesProvider()
    {
        IRGBDevice[] sdkDevices = RgbDeviceProvider.Devices.ToArray();
        IRGBDevice[] hostDevices = _deviceService.Devices
            .Where(device => ReferenceEquals(device.DeviceProvider, this))
            .Select(device => device.RgbDevice)
            .ToArray();
        return sdkDevices.Length == hostDevices.Length &&
               sdkDevices.All(sdkDevice => hostDevices.Any(hostDevice => ReferenceEquals(hostDevice, sdkDevice)));
    }

    private async Task ReloadHostProviderAsync()
    {
        // Stock Artemis only enumerates devices when the provider is registered. It has no
        // per-device rebind hook, so publish a settled SDK change through that public path.
        // Matching device names/LED IDs let stock Artemis restore saved layout and bindings.
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_running)
                    return;

                _logger.Information("OpenRGB device list settled; reloading provider for baseline Artemis");
                Disable();
                Enable();
            });
        }
        catch (Exception exception)
        {
            _logger.Error(exception, "OpenRGB provider reload failed after a device-list change");
        }
    }

    private TimeSpan GetReconnectDelay() =>
        RgbDeviceProvider.DeviceDefinitions.Any(definition => !definition.Connected)
            ? ConnectionRetryDelay
            : SnapshotRetryDelay;

    private void ScheduleReconnect(TimeSpan delay)
    {
        if (!_running)
            return;

        _reconnectTimer.Stop();
        _reconnectTimer.Interval = delay.TotalMilliseconds;
        _reconnectTimer.Start();
    }

    private static string GetDefinitionKey(OpenRGBServerDefinition definition) => $"{definition.Ip}:{definition.Port}";
}
