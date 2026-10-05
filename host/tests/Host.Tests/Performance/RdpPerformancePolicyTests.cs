using System.Diagnostics;
using System.Text;
using System.Xml.Linq;
using HyperHarbor.Host.Core.Performance;
using HyperHarbor.Host.Core.Provisioning;

namespace HyperHarbor.Host.Tests.Performance;

public sealed class RdpPerformancePolicyTests
{
    private static readonly string AdmxPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"PolicyDefinitions\TerminalServer.admx");

    /// <summary>
    /// Every policy value is checked against TerminalServer.admx on this machine: the policy exists, its key
    /// is the one written, the value name belongs to it (on the policy or one of its elements), and the
    /// value is one the policy defines. Skipped when the ADMX is not installed.
    /// </summary>
    [Fact]
    public void PolicyValues_MatchTerminalServerAdmx()
    {
        if (!File.Exists(AdmxPath))
        {
            return;
        }

        var admx = XDocument.Load(AdmxPath);
        XNamespace ns = admx.Root!.Name.Namespace;
        foreach (var value in RdpPerformancePolicy.Values(hardwareEncoding: true).Where(value => value.Policy is not null))
        {
            var policy = admx.Descendants(ns + "policy").SingleOrDefault(element => (string?)element.Attribute("name") == value.Policy);
            Assert.True(policy is not null, $"{value.Policy} is not in TerminalServer.admx.");
            Assert.Equal(value.Key.Replace("HKLM:\\", string.Empty, StringComparison.Ordinal), (string?)policy.Attribute("key"));

            var owner = (string?)policy.Attribute("valueName") == value.Name
                ? policy
                : policy.Descendants().FirstOrDefault(element => (string?)element.Attribute("valueName") == value.Name);
            Assert.True(owner is not null, $"{value.Name} is not a value of {value.Policy}.");

            var defined = owner.Descendants(ns + "decimal").Select(element => (int?)element.Attribute("value")).ToList();
            if (owner == policy)
            {
                // An on/off policy: enabled is 1 unless it says otherwise.
                defined = defined.Count > 0 ? defined : [1, 0];
            }

            Assert.Contains(value.Value, defined.Select(item => item ?? -1));
        }
    }

    [Fact]
    public void ImageQualityIsHigh_CompressionIsOff_AndTheFrameLimitIs60Fps()
    {
        var values = RdpPerformancePolicy.Values(hardwareEncoding: false).ToDictionary(value => value.Name, value => value.Value);

        Assert.Equal(2, values["ImageQuality"]);
        Assert.Equal(0, values["MaxCompressionLevel"]);
        Assert.Equal(1, values["bEnumerateHWBeforeSW"]);
        Assert.Equal(1, values["AVC444ModePreferred"]);
        Assert.Equal(15, values["DWMFRAMEINTERVAL"]);
    }

    [Fact]
    public void HardwareEncoding_IsOffUnlessChosen()
    {
        Assert.Equal(0, RdpPerformancePolicy.Values(false).Single(value => value.Name == "AVCHardwareEncodePreferred").Value);
        Assert.Equal(1, RdpPerformancePolicy.Values(true).Single(value => value.Name == "AVCHardwareEncodePreferred").Value);
    }

    /// <summary>The host-side scripts parse in Windows PowerShell, so a syntax error cannot reach a guest.</summary>
    [Theory]
    [InlineData("performance")]
    [InlineData("accounts")]
    [InlineData("appx")]
    public void HostScripts_ParseInWindowsPowerShell(string which)
    {
        var script = which switch
        {
            "performance" => PowerShellDirectPerformanceSetup.Script,
            "accounts" => PowerShellDirectAccountManager.Script,
            _ => Core.Profiles.PowerShellDirectProfileReader.AppxScript,
        };
        var check = "$e = $null; [void][System.Management.Automation.Language.Parser]::ParseInput([Console]::In.ReadToEnd(), [ref]$null, [ref]$e); $e.Count";
        var start = new ProcessStartInfo("powershell.exe") { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(check)) })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        process.StandardInput.Write(script);
        process.StandardInput.Close();
        var errors = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();

        Assert.Equal("0", errors);
    }
}
