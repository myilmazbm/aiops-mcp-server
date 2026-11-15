using System.Text;
using System.Text.RegularExpressions;

namespace AiOps.Mcp.Directory;

/// <summary>
/// Turns the resolve_servers filter argument into a search base + LDAP filter.
/// The filter is either a distinguished name ("OU=SQL,DC=contoso,DC=local") or a
/// name pattern ("SQL*", "web"); patterns without a wildcard are wrapped in *…*.
/// </summary>
public static partial class LdapQueryBuilder
{
    [GeneratedRegex(@"^\s*(OU|CN|DC)\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex DnStartRegex();

    public static (string? SearchBase, string LdapFilter) Build(string filter)
    {
        filter = filter.Trim();

        if (DnStartRegex().IsMatch(filter))
            return (filter, BuildComputerFilter("*"));

        var pattern = filter.Contains('*') ? filter : $"*{filter}*";
        return (null, BuildComputerFilter(pattern));
    }

    /// <summary>userAccountControl bit SERVER_TRUST_ACCOUNT: a writable domain controller.</summary>
    private const int ServerTrustAccount = 8192;

    /// <summary>
    /// userAccountControl bit PARTIAL_SECRETS_ACCOUNT: a read-only domain controller. An RODC
    /// does NOT carry SERVER_TRUST_ACCOUNT — its computer account is a workstation trust
    /// account (4096) with this bit added — so excluding 8192 alone lets RODCs through, and
    /// they are domain controllers for the purposes of the out-of-scope policy.
    /// </summary>
    private const int PartialSecretsAccount = 67108864;

    private static string BuildComputerFilter(string namePattern)
    {
        var escaped = EscapeFilterValue(namePattern);
        // Server-OS computer accounts only (the fleet has no workstations), excluding domain
        // controllers: DCs are out of scope by policy — a JEA virtual account on a DC runs
        // with domain-controller privileges — so the agent must never even be offered them.
        // 1.2.840.113556.1.4.803 = LDAP_MATCHING_RULE_BIT_AND.
        return "(&(objectCategory=computer)(operatingSystem=*Server*)" +
               $"(!(userAccountControl:1.2.840.113556.1.4.803:={ServerTrustAccount}))" +
               $"(!(userAccountControl:1.2.840.113556.1.4.803:={PartialSecretsAccount}))" +
               $"(|(cn={escaped})(dNSHostName={escaped})))";
    }

    /// <summary>
    /// RFC 4515 escaping, keeping '*' as a user wildcard. Injection like
    /// "x)(objectClass=*" is neutralized because parentheses are escaped.
    /// </summary>
    internal static string EscapeFilterValue(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\': sb.Append(@"\5c"); break;
                case '(': sb.Append(@"\28"); break;
                case ')': sb.Append(@"\29"); break;
                case '\0': sb.Append(@"\00"); break;
                default: sb.Append(ch); break;
            }
        }

        return sb.ToString();
    }
}
