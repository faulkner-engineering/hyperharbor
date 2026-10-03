namespace HyperHarbor.Shared.Contracts;

/// <summary>
/// Facts about the API contract shared by host and client.
/// </summary>
public static class ContractInfo
{
    /// <summary>Matches info.version in docs/api.yaml.</summary>
    public const string ApiVersion = "1.1.0";

    /// <summary>Base path of all API routes.</summary>
    public const string BasePath = "/api/v1";
}
