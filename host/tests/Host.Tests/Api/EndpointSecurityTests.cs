using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using HyperHarbor.Host.Service.Audit;
using HyperHarbor.Host.Service.Security;
using HyperHarbor.Shared.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using YamlDotNet.RepresentationModel;

namespace HyperHarbor.Host.Tests.Api;

/// <summary>
/// Checks every mapped endpoint, so a new route is covered without anyone remembering to add a test:
/// the routes match api.yaml, only the operations api.yaml marks with "security: []" are anonymous,
/// and every other route rejects a request without a paired client certificate.
/// </summary>
public sealed partial class EndpointSecurityTests : IDisposable
{
    private static readonly string[] AnonymousOperations = ["createPairingRequest", "cancelPairingRequest", "confirmPairing"];

    private readonly TestHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public void MappedRoutes_MatchContract()
    {
        var mapped = Endpoints(_host).Select(endpoint => endpoint.Key).Order().ToList();
        var contract = ContractOperations().Select(operation => operation.Key).Order().ToList();

        Assert.Equal(contract, mapped);
    }

    [Fact]
    public void AnonymousEndpoints_AreExactlyThePairingHandshake()
    {
        var anonymous = Endpoints(_host)
            .Where(endpoint => endpoint.Value.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(endpoint => OperationId(endpoint.Value))
            .Order();
        var contractAnonymous = ContractOperations()
            .Where(operation => operation.Value.Anonymous)
            .Select(operation => operation.Value.OperationId)
            .Order();

        Assert.Equal(AnonymousOperations.Order(), anonymous);
        Assert.Equal(AnonymousOperations.Order(), contractAnonymous);
    }

    [Fact]
    public void EveryStateChangingEndpoint_IsAudited()
    {
        var unaudited = Endpoints(_host)
            .Where(endpoint => !endpoint.Key.StartsWith("GET ", StringComparison.Ordinal))
            .Where(endpoint => endpoint.Value.Metadata.GetMetadata<AuditedMetadata>() is null)
            .Select(endpoint => endpoint.Key);

        Assert.Empty(unaudited);
    }

    [Fact]
    public void ElevatedEndpoints_MatchContract()
    {
        var elevated = Endpoints(_host)
            .Where(endpoint => endpoint.Value.Metadata.GetMetadata<ElevationRequiredMetadata>() is { Conditional: false })
            .Select(endpoint => OperationId(endpoint.Value))
            .Order();
        var contractElevated = ContractOperations()
            .Where(operation => operation.Value.Elevated)
            .Select(operation => operation.Value.OperationId)
            .Order();

        Assert.Equal(contractElevated, elevated);
    }

    /// <summary>
    /// OpenAPI cannot express elevation that depends on the request body, so these operations
    /// document it in their description instead. Adding one here means documenting it there.
    /// </summary>
    [Fact]
    public void ConditionallyElevatedEndpoints_AreTheDocumentedOnes()
    {
        var conditional = Endpoints(_host)
            .Where(endpoint => endpoint.Value.Metadata.GetMetadata<ElevationRequiredMetadata>() is { Conditional: true })
            .Select(endpoint => OperationId(endpoint.Value));

        Assert.Equal(["performVmAction"], conditional);
    }

    [Fact]
    public void ElevatedEndpoints_AreAudited()
    {
        var unaudited = Endpoints(_host)
            .Where(endpoint => endpoint.Value.Metadata.GetMetadata<ElevationRequiredMetadata>() is not null)
            .Where(endpoint => endpoint.Value.Metadata.GetMetadata<AuditedMetadata>() is null)
            .Select(endpoint => endpoint.Key);

        Assert.Empty(unaudited);
    }

    [Fact]
    public void ReadOnlyEndpoints_AreNotAudited()
    {
        var audited = Endpoints(_host)
            .Where(endpoint => endpoint.Key.StartsWith("GET ", StringComparison.Ordinal))
            .Where(endpoint => endpoint.Value.Metadata.GetMetadata<AuditedMetadata>() is not null)
            .Select(endpoint => endpoint.Key);

        Assert.Empty(audited);
    }

    public static TheoryData<string> ProtectedOperations()
    {
        var data = new TheoryData<string>();
        foreach (var (key, operation) in ContractOperations().Where(operation => !operation.Value.Anonymous))
        {
            data.Add(key);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ProtectedOperations))]
    public async Task ProtectedEndpoint_WithoutCertificate_Returns401(string operation)
    {
        using var client = _host.CreateClient();

        var response = await client.SendAsync(Request(operation));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [MemberData(nameof(ProtectedOperations))]
    public async Task ProtectedEndpoint_WithUnpairedCertificate_Returns401(string operation)
    {
        using var certificate = TestHost.CreateClientCertificate("Unpaired");
        using var client = _host.CreateClient(certificate);

        var response = await client.SendAsync(Request(operation));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(ProtectedOperations))]
    public async Task ProtectedEndpoint_AfterDeviceRemoved_Returns401(string operation)
    {
        using var certificate = TestHost.CreateClientCertificate("Revoked");
        var device = _host.Pair(certificate);
        _host.Services.GetRequiredService<Core.Security.PairedDeviceStore>().Remove(device.DeviceId);
        using var client = _host.CreateClient(certificate);

        var response = await client.SendAsync(Request(operation));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>"METHOD /path" with route parameters filled in, and an empty JSON body for writes.</summary>
    private static HttpRequestMessage Request(string operation)
    {
        var space = operation.IndexOf(' ', StringComparison.Ordinal);
        var method = new HttpMethod(operation[..space]);
        var path = RouteParameter().Replace(operation[(space + 1)..], _ => Guid.NewGuid().ToString());
        var request = new HttpRequestMessage(method, path);
        if (method == HttpMethod.Post)
        {
            request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        }

        return request;
    }

    /// <summary>Mapped route endpoints keyed "METHOD /api/v1/path/{param}", constraints removed.</summary>
    private static Dictionary<string, RouteEndpoint> Endpoints(TestHost host) => host.Services
        .GetRequiredService<EndpointDataSource>()
        .Endpoints
        .OfType<RouteEndpoint>()
        .SelectMany(endpoint => (endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()?.HttpMethods ?? [])
            .Select(method => (Key: $"{method} {NormalizeRoute(endpoint.RoutePattern.RawText!)}", Endpoint: endpoint)))
        .ToDictionary(pair => pair.Key, pair => pair.Endpoint);

    private static string NormalizeRoute(string route)
    {
        var withoutConstraints = RouteConstraint().Replace(route, "}");
        var trimmed = withoutConstraints.TrimEnd('/');
        return trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }

    private static string OperationId(RouteEndpoint endpoint) =>
        endpoint.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName
        ?? throw new InvalidOperationException($"{endpoint.RoutePattern.RawText} has no name; name it after its api.yaml operationId.");

    private sealed record ContractOperation(string OperationId, bool Anonymous, bool Elevated);

    /// <summary>api.yaml operations keyed "METHOD /api/v1/path/{param}".</summary>
    private static Dictionary<string, ContractOperation> ContractOperations()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "api.yaml");
        using var reader = new StreamReader(path);
        var stream = new YamlStream();
        stream.Load(reader);
        var root = (YamlMappingNode)stream.Documents[0].RootNode;

        var operations = new Dictionary<string, ContractOperation>();
        foreach (var (pathNode, item) in (YamlMappingNode)root["paths"])
        {
            foreach (var (methodNode, operationNode) in (YamlMappingNode)item)
            {
                var method = ((YamlScalarNode)methodNode).Value!;
                if (method is "parameters" or "summary" or "description")
                {
                    continue;
                }

                var operation = (YamlMappingNode)operationNode;
                var operationId = ((YamlScalarNode)operation["operationId"]).Value!;
                var anonymous = operation.Children.TryGetValue(new YamlScalarNode("security"), out var security)
                    && security is YamlSequenceNode { Children.Count: 0 };
                var route = ContractInfo.BasePath + ((YamlScalarNode)pathNode).Value!;
                var elevated = security is YamlSequenceNode requirements
                    && requirements.Children.OfType<YamlMappingNode>().Any(requirement => requirement.Children.ContainsKey(new YamlScalarNode("elevation")));
                operations.Add($"{method.ToUpperInvariant()} {route.TrimEnd('/')}", new ContractOperation(operationId, anonymous, elevated));
            }
        }

        return operations;
    }

    [GeneratedRegex(@":[^}]+}")]
    private static partial Regex RouteConstraint();

    [GeneratedRegex(@"\{[^}]+\}")]
    private static partial Regex RouteParameter();
}
