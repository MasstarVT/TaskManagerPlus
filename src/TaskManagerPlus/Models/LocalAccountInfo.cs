namespace TaskManagerPlus.Models;

/// <summary>
/// One local (non-domain) Windows account (#891) - enabled/disabled state, Administrators-group
/// membership, the two WMI-visible password flags, and whether the account is hidden from the
/// sign-in screen via the Winlogon\SpecialAccounts\UserList registry key. See
/// Services/AccountSecurityService.ReadLocalAccounts.
///
/// "Degrade to Unknown, never fabricate": <see cref="PasswordRequired"/> reflects only
/// Win32_UserAccount's own PasswordRequired flag - this app never attempts to authenticate, so it
/// cannot and does not claim to know whether a blank password is *currently* set, only whether
/// Windows requires one at all.
/// </summary>
public sealed class LocalAccountInfo
{
    public string Name { get; init; } = string.Empty;
    public string Sid { get; init; } = string.Empty;
    public bool Enabled { get; init; }
    public bool IsAdministrator { get; init; }

    /// <summary>Win32_UserAccount.PasswordRequired - false means "Windows does not require a
    /// password for this account," reported exactly that way, never as "password is blank."</summary>
    public bool PasswordRequired { get; init; } = true;

    public bool PasswordExpires { get; init; } = true;

    /// <summary>True when a DWORD value named exactly this account's name exists under
    /// Winlogon\SpecialAccounts\UserList with data 0 - the documented "hide this account from the
    /// sign-in screen" flag.</summary>
    public bool IsHiddenFromSignInScreen { get; init; }

    /// <summary>#891's own high-value combination: hidden AND an Administrator AND enabled - a
    /// Medium/High finding is raised separately for this in AccountSecurityService, this flag just
    /// lets the grid highlight the row too.</summary>
    public bool IsHighValueCombination => IsHiddenFromSignInScreen && IsAdministrator && Enabled;
}
