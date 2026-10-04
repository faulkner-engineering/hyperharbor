using HyperHarbor.Shared.Contracts;

namespace HyperHarbor.Host.Core.VmConsole;

/// <summary>A console request that cannot proceed in the host's or VM's current state (409, with a problem code).</summary>
public sealed class ConsoleConflictException : Exception
{
    public ConsoleConflictException(string message, string code)
        : base(message)
    {
        Code = code;
    }

    public string Code { get; }

    public static ConsoleConflictException SetupRequired(string reason) => new(
        $"{reason} Open the HyperHarbor Host window on the host and choose Set up console access.",
        ContractInfo.ProblemCodes.ConsoleSetupRequired);
}

/// <summary>A tunnel request whose console ticket is missing, expired, or issued to another device or VM (403).</summary>
public sealed class ConsoleTicketRejectedException : Exception
{
    public ConsoleTicketRejectedException()
        : base("The console ticket is missing, expired, or was issued for another device or VM. Open the console again.")
    {
    }
}

/// <summary>The host's Virtual Machine Connection service could not be reached (502).</summary>
public sealed class ConsoleUnavailableException : Exception
{
    public ConsoleUnavailableException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}
