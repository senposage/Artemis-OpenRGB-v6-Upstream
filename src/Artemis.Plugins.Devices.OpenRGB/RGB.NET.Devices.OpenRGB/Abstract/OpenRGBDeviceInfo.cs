using RGB.NET.Core;
using OpenRGBDevice = OpenRGB.NET.Device;

namespace RGB.NET.Devices.OpenRGB;

/// <summary>
/// Represents generic information for an OpenRGB Device
/// </summary>
public class OpenRGBDeviceInfo : IRGBDeviceInfo
{
    #region Properties & Fields

    /// <inheritdoc />
    public RGBDeviceType DeviceType { get; }

    /// <inheritdoc />
    public string DeviceName { get; }

    /// <inheritdoc />
    public string Manufacturer { get; }

    /// <inheritdoc />
    public string Model { get; }

    /// <inheritdoc />
    public object? LayoutMetadata { get; set; }

    /// <summary>
    /// Gets the OpenRGB device.
    /// </summary>
    public OpenRGBDevice OpenRGBDevice { get; }

    /// <summary>Gets the configured OpenRGB server identity.</summary>
    public string ServerIdentity { get; }

    /// <summary>Gets the protocol-v6 controller ID supplied by OpenRGB.</summary>
    public uint ControllerId { get; }

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of <see cref="OpenRGBDeviceInfo"/>.
    /// </summary>
    /// <param name="openRGBDevice">The OpenRGB device to extract information from.</param>
    internal OpenRGBDeviceInfo(OpenRGBDevice openRGBDevice, string serverIdentity, uint controllerId, string? partName = null)
    {
        this.OpenRGBDevice = openRGBDevice;
        ServerIdentity = serverIdentity;
        ControllerId = controllerId;

        DeviceType = Helper.GetRgbNetDeviceType(openRGBDevice.Type);
        Manufacturer = Helper.GetVendorName(openRGBDevice);
        Model = Helper.GetModelName(openRGBDevice);
        // Stock Artemis derives its device identifier from DeviceName, Manufacturer,
        // Model and DeviceType. Give each OpenRGB zone a distinct, stable name.
        DeviceName = DeviceHelper.CreateDeviceName(Manufacturer, Model) +
                     (partName == null ? string.Empty : $" [{partName}]");
    }

    #endregion
}
