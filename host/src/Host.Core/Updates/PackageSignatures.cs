namespace HyperHarbor.Host.Core.Updates;

public sealed record SignatureCheck(bool Accepted, string Detail);

/// <summary>
/// Checks a downloaded package's signatures. It runs after the SHA-256 matched and before the self-test.
/// </summary>
public interface IPackageSignatureVerifier
{
    Task<SignatureCheck> VerifyAsync(string packagePath, UpdateManifest manifest, CancellationToken cancellationToken);
}

/// <summary>
/// The verifier until releases are signed. The SHA-256 from the manifest protects against corruption and a
/// tampered mirror, not against a compromised repository or account; signatures close that gap.
/// </summary>
public sealed class UnsignedPackageVerifier : IPackageSignatureVerifier
{
    public Task<SignatureCheck> VerifyAsync(string packagePath, UpdateManifest manifest, CancellationToken cancellationToken)
    {
        // SIGNING HOOK (Phase 11, deferred): verify here, and return Accepted = false on any failure:
        //   - Authenticode: WinVerifyTrust on packagePath, with the signer pinned to HyperHarbor's code signing
        //     certificate (not just any trusted publisher).
        //   - Sigstore: the bundle described in manifest.Verification, checked against the release workflow's
        //     identity (repository faulkner-engineering/hyperharbor) and the package's SHA-256.
        return Task.FromResult(new SignatureCheck(true, "Signatures are not checked yet; the package matched the manifest's SHA-256."));
    }
}
