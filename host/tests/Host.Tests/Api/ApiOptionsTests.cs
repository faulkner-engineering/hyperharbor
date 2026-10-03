using System.ComponentModel.DataAnnotations;
using HyperHarbor.Host.Service.Api;

namespace HyperHarbor.Host.Tests.Api;

public class ApiOptionsTests
{
    [Fact]
    public void Defaults_ListenOnAllInterfacesOnTheContractPort()
    {
        var options = new ApiOptions();

        Assert.Null(options.ListenAddress);
        Assert.Equal(48443, options.Port);
        Assert.True(IsValid(options));
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("localhost", false)]
    [InlineData("0.0.0.0:80", false)]
    public void ListenAddress_MustBeAnIpAddress(string address, bool valid)
    {
        Assert.Equal(valid, IsValid(new ApiOptions { ListenAddress = address }));
    }

    private static bool IsValid(ApiOptions options) =>
        Validator.TryValidateObject(options, new ValidationContext(options), [], validateAllProperties: true);
}
