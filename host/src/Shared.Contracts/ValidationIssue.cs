namespace HyperHarbor.Shared.Contracts;

/// <summary>One problem with a request field. Schema: ValidationIssue.</summary>
/// <param name="Field">The request property, for example "processorCount".</param>
public sealed record ValidationIssue(string Field, string Message);
