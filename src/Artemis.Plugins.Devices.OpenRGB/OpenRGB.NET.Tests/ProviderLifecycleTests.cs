using RGB.NET.Devices.OpenRGB;
using Xunit;

namespace OpenRGB.NET.Tests;

public class ProviderLifecycleTests
{
    [Fact]
    public void ChangedProtocolIdIsRemoveAndAdd()
    {
        (uint[] removed, uint[] added) = OpenRGBDeviceProvider.DiffControllerIds([10, 11], [10, 99]);

        Assert.Equal([11u], removed);
        Assert.Equal([99u], added);
    }

    [Fact]
    public void DisposedProviderCanBeRecreated()
    {
        OpenRGBDeviceProvider provider = OpenRGBDeviceProvider.Instance;
        provider.Dispose();

        OpenRGBDeviceProvider replacement = OpenRGBDeviceProvider.Instance;

        Assert.NotSame(provider, replacement);
        replacement.Dispose();
    }

}
