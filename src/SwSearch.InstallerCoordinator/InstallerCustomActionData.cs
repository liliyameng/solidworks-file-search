using System.Globalization;
using System.Security.Principal;
using System.Text;

namespace SwSearch.InstallerCoordinator;

internal static class InstallerContextContract
{
    internal const string RegisteredRootPolicy = "registered-component-final-program-files";
    internal const string MachineOnlyMutexSidMarker = "MACHINE-ONLY";
    internal const int MaximumCustomActionDataLength = 1024;
    internal static readonly TimeSpan MaximumDeadlineHorizon = TimeSpan.FromSeconds(45);

    internal static bool TryNormalizeIndividualAccountSid(
        string value,
        out string normalized,
        out string error)
    {
        normalized = string.Empty;
        error = string.Empty;
        SecurityIdentifier sid;
        try
        {
            sid = new SecurityIdentifier(value);
        }
        catch (ArgumentException)
        {
            error = "Install user SID is invalid.";
            return false;
        }

        if (!sid.IsAccountSid() ||
            sid.IsWellKnown(WellKnownSidType.LocalSystemSid) ||
            sid.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) ||
            sid.IsWellKnown(WellKnownSidType.WorldSid) ||
            sid.IsWellKnown(WellKnownSidType.AuthenticatedUserSid))
        {
            error = "Install user SID must identify one Windows account.";
            return false;
        }

        normalized = sid.Value;
        return true;
    }

    internal static bool TryParseOperation(string value, out InstallerOperation operation)
    {
        operation = value switch
        {
            "install" => InstallerOperation.Install,
            "upgrade" => InstallerOperation.Upgrade,
            "repair" => InstallerOperation.Repair,
            "uninstall" => InstallerOperation.Uninstall,
            _ => default
        };
        return value is "install" or "upgrade" or "repair" or "uninstall";
    }

    internal static bool IsCryptographicSessionNonce(Guid nonce)
    {
        Span<byte> bytes = stackalloc byte[16];
        nonce.TryWriteBytes(bytes);
        return (bytes[7] & 0xF0) == 0x40 && (bytes[8] & 0xC0) == 0x80;
    }
}

internal static class InstallerCustomActionData
{
    private static readonly HashSet<string> AllowedKeys = new(StringComparer.Ordinal)
    {
        "v", "user", "sid", "session", "upgrade", "nonce", "root", "action", "deadline"
    };

    internal static bool TryParse(
        string customActionData,
        out InstallerCoordinatorCommand? command,
        out string error)
    {
        command = null;
        error = string.Empty;
        if (string.IsNullOrEmpty(customActionData) ||
            customActionData.Length > InstallerContextContract.MaximumCustomActionDataLength)
        {
            error = "CustomActionData is empty or too long.";
            return false;
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in customActionData.Split(';', StringSplitOptions.None))
        {
            var separator = field.IndexOf('=');
            if (separator <= 0 || separator != field.LastIndexOf('='))
            {
                error = "CustomActionData field is malformed.";
                return false;
            }

            var key = field[..separator];
            if (!AllowedKeys.Contains(key) || values.ContainsKey(key))
            {
                error = "CustomActionData contains an unknown or duplicate key.";
                return false;
            }

            if (!TryPercentDecode(field[(separator + 1)..], out var decoded))
            {
                error = "CustomActionData contains invalid escaping.";
                return false;
            }

            values.Add(key, decoded);
        }

        if (values.Count != AllowedKeys.Count || values["v"] != "2")
        {
            error = "CustomActionData schema is incomplete or unsupported.";
            return false;
        }

        var hasInstallUserContext = values["user"] switch
        {
            "1" => true,
            "0" => false,
            _ => (bool?)null
        };
        if (hasInstallUserContext is null)
        {
            error = "CustomActionData user-context flag is invalid.";
            return false;
        }

        var sid = InstallerContextContract.MachineOnlyMutexSidMarker;
        var nonce = Guid.Empty;
        var sessionId = -1;
        if (hasInstallUserContext.Value)
        {
            if (!InstallerContextContract.TryNormalizeIndividualAccountSid(
                    values["sid"], out sid, out error))
            {
                return false;
            }
            if (!Guid.TryParse(values["nonce"], out nonce) ||
                !InstallerContextContract.IsCryptographicSessionNonce(nonce))
            {
                error = "CustomActionData nonce is invalid.";
                return false;
            }
            if (!int.TryParse(
                    values["session"],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out sessionId) || sessionId <= 0)
            {
                error = "CustomActionData session ID is invalid.";
                return false;
            }
        }
        else if (values["sid"].Length != 0 ||
                 values["session"].Length != 0 ||
                 values["nonce"].Length != 0)
        {
            error = "Machine-only CustomActionData must not contain user identity fields.";
            return false;
        }

        if (!Guid.TryParse(values["upgrade"], out var upgradeCode) || upgradeCode == Guid.Empty)
        {
            error = "CustomActionData UpgradeCode is invalid.";
            return false;
        }

        if (!string.Equals(
                values["root"],
                InstallerContextContract.RegisteredRootPolicy,
                StringComparison.Ordinal))
        {
            error = "CustomActionData root policy is invalid.";
            return false;
        }

        if (!InstallerContextContract.TryParseOperation(values["action"], out var operation))
        {
            error = "CustomActionData install action is invalid.";
            return false;
        }

        if (!long.TryParse(
                values["deadline"],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var deadlineTick) || deadlineTick <= 0)
        {
            error = "CustomActionData deadline is invalid.";
            return false;
        }

        command = new InstallerCoordinatorCommand(
            InstallerCoordinatorMode.PrivilegedHandle,
            upgradeCode.ToString("B").ToUpperInvariant(),
            InstallerContextContract.RegisteredRootPolicy,
            Path.Combine(Path.GetTempPath(), "SwSearch-InstallerCoordinator.log"),
            sid,
            nonce,
            operation,
            deadlineTick,
            sessionId,
            hasInstallUserContext.Value);
        return true;
    }

    internal static InstallerCoordinatorExitCode GetDiagnosticExitCode(string error) => error switch
    {
        "CustomActionData is empty or too long." => InstallerCoordinatorExitCode.CadEnvelopeInvalid,
        "CustomActionData field is malformed." => InstallerCoordinatorExitCode.CadSchemaInvalid,
        "CustomActionData contains an unknown or duplicate key." => InstallerCoordinatorExitCode.CadSchemaInvalid,
        "CustomActionData contains invalid escaping." => InstallerCoordinatorExitCode.CadSchemaInvalid,
        "CustomActionData schema is incomplete or unsupported." => InstallerCoordinatorExitCode.CadSchemaInvalid,
        "CustomActionData user-context flag is invalid." => InstallerCoordinatorExitCode.CadUserFlagInvalid,
        "Machine-only CustomActionData must not contain user identity fields." =>
            InstallerCoordinatorExitCode.CadMachineIdentityInvalid,
        "CustomActionData UpgradeCode is invalid." => InstallerCoordinatorExitCode.CadUpgradeInvalid,
        "CustomActionData root policy is invalid." => InstallerCoordinatorExitCode.CadRootPolicyInvalid,
        "CustomActionData install action is invalid." => InstallerCoordinatorExitCode.CadActionInvalid,
        "CustomActionData deadline is invalid." => InstallerCoordinatorExitCode.CadDeadlineInvalid,
        "Install user SID is invalid." => InstallerCoordinatorExitCode.CadUserIdentityInvalid,
        "Install user SID must identify one Windows account." => InstallerCoordinatorExitCode.CadUserIdentityInvalid,
        "CustomActionData nonce is invalid." => InstallerCoordinatorExitCode.CadUserIdentityInvalid,
        "CustomActionData session ID is invalid." => InstallerCoordinatorExitCode.CadUserIdentityInvalid,
        _ => InstallerCoordinatorExitCode.ContextDataInvalid
    };

    private static bool TryPercentDecode(string value, out string decoded)
    {
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '%')
            {
                if (index + 2 >= value.Length ||
                    !TryHex(value[index + 1], out var high) ||
                    !TryHex(value[index + 2], out var low))
                {
                    decoded = string.Empty;
                    return false;
                }

                character = (char)((high << 4) | low);
                index += 2;
            }

            if (char.IsControl(character) || character is ';' or '=')
            {
                decoded = string.Empty;
                return false;
            }

            builder.Append(character);
        }

        decoded = builder.ToString();
        return true;
    }

    private static bool TryHex(char value, out int result)
    {
        if (value is >= '0' and <= '9')
        {
            result = value - '0';
            return true;
        }

        if (value is >= 'A' and <= 'F')
        {
            result = value - 'A' + 10;
            return true;
        }

        if (value is >= 'a' and <= 'f')
        {
            result = value - 'a' + 10;
            return true;
        }

        result = 0;
        return false;
    }
}
