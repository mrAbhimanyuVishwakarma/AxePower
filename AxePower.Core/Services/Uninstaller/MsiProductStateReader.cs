using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace AxePower.Core.Services.Uninstaller;

/// <summary>Asks Windows Installer whether an MSI product is registered.</summary>
public interface IMsiProductStateReader
{
    /// <summary>
    /// Raw <c>MsiQueryProductState</c> result: 5 installed, 1 advertised, 2 installed for another
    /// user, −1 not registered. Returns −2 (invalid argument) for a malformed ProductCode.
    /// </summary>
    int QueryProductState(string productCode);
}

public sealed partial class MsiProductStateReader : IMsiProductStateReader
{
    public const int InstallStateUnknown = -1;
    public const int InstallStateInvalidArg = -2;
    public const int InstallStateAdvertised = 1;
    public const int InstallStateAbsent = 2;
    public const int InstallStateDefault = 5;

    [DllImport("msi.dll", CharSet = CharSet.Unicode, EntryPoint = "MsiQueryProductStateW")]
    private static extern int MsiQueryProductState(string szProduct);

    public int QueryProductState(string productCode)
    {
        if (!IsProductCode(productCode)) return InstallStateInvalidArg;
        try
        {
            return MsiQueryProductState(productCode);
        }
        catch
        {
            // msi.dll missing or broken: we can't tell, so don't call it unregistered
            return InstallStateDefault;
        }
    }

    /// <summary>True when the state means the product is still registered on this machine.</summary>
    public static bool IsRegistered(int state) =>
        state is InstallStateDefault or InstallStateAdvertised or InstallStateAbsent;

    public static bool IsProductCode(string? value) =>
        !string.IsNullOrWhiteSpace(value) && ProductCodeRegex().IsMatch(value);

    [GeneratedRegex(@"^\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}$")]
    private static partial Regex ProductCodeRegex();
}
