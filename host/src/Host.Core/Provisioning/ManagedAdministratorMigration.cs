using System.Security.Cryptography;
using System.Text.Json;
using HyperHarbor.Host.Core.Unattend;
using HyperHarbor.Shared.Contracts.Unattend;

namespace HyperHarbor.Host.Core.Provisioning;

/// <summary>
/// Data format 5: a stored administrator credential says whether HyperHarbor made the account
/// (<see cref="GuestCredential.Managed"/>), and the host keeps such an account's password from expiring. Credentials
/// stored before that carry no mark, so the ones an unattended install created get it here: the VM has an install
/// record and the account still has the name installs give it. An install with another administrator name, or a VM
/// that was set up again with the user's own account, is left unmarked; the VM list then warns before that password
/// expires instead of changing the account.
/// </summary>
public static class ManagedAdministratorMigration
{
    /// <returns>How many credentials were marked.</returns>
    public static int Run(string dataDirectory)
    {
        try
        {
            var credentials = new VmCredentialStore(dataDirectory);
            var marked = 0;
            foreach (var install in new UnattendedInstallStore(dataDirectory).List())
            {
                if (credentials.Find(install.VmId) is { Managed: false } credential
                    && string.Equals(credential.UserName.Trim(), UnattendProfile.DefaultAdminAccountName, StringComparison.OrdinalIgnoreCase))
                {
                    credentials.Save(install.VmId, credential with { Managed = true });
                    marked++;
                }
            }

            return marked;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or InvalidDataException)
        {
            // Files this step cannot read are left as they are. An unmarked account is only warned about, never
            // locked out silently, so the host starts either way.
            return 0;
        }
    }
}
