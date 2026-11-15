using System.DirectoryServices.Protocols;
using Microsoft.Extensions.Options;

namespace AiOps.Mcp.Directory;

public sealed record AdComputer(string Name, string? DnsHostName, string? OperatingSystem, string? DistinguishedName);

public interface IDirectoryGateway
{
    /// <summary>Runs an LDAP computer search; results are capped at maxResults.</summary>
    Task<IReadOnlyList<AdComputer>> SearchComputersAsync(
        string? searchBase, string ldapFilter, int maxResults, CancellationToken cancellationToken);
}

public sealed class DirectoryOptions
{
    public const string SectionName = "Directory";

    /// <summary>Domain controller or domain DNS name; empty uses the machine's own domain (serverless bind).</summary>
    public string? LdapServer { get; set; }

    /// <summary>Search base DN; empty uses the domain's defaultNamingContext.</summary>
    public string? DefaultSearchBase { get; set; }

    public int PageSize { get; set; } = 500;

    /// <summary>
    /// Bind and search timeout. Without it the protocol API waits on its own defaults, and
    /// because the work runs on a thread pool thread that cannot be interrupted, a domain
    /// controller that accepts the connection and then goes quiet makes resolve_servers hang
    /// with no way for the caller to give up.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// LDAP gateway over System.DirectoryServices.Protocols. Binds with the service identity
/// (gMSA) via Negotiate — no credentials anywhere.
/// </summary>
public sealed class LdapDirectoryGateway(IOptions<DirectoryOptions> options) : IDirectoryGateway
{
    private static readonly string[] Attributes = ["cn", "dNSHostName", "operatingSystem", "distinguishedName"];

    private readonly DirectoryOptions _options = options.Value;

    public Task<IReadOnlyList<AdComputer>> SearchComputersAsync(
        string? searchBase, string ldapFilter, int maxResults, CancellationToken cancellationToken)
        // The protocol API is synchronous; searches are short but must not block the caller
        // thread. The token is passed to Task.Run only to skip work not yet started — once
        // Bind() is running nothing can interrupt it — so WaitAsync is what actually lets the
        // caller give up, and LdapConnection.Timeout is what bounds the orphaned thread.
        => Task.Run<IReadOnlyList<AdComputer>>(() => Search(searchBase, ldapFilter, maxResults), cancellationToken)
            .WaitAsync(cancellationToken);

    private List<AdComputer> Search(string? searchBase, string ldapFilter, int maxResults)
    {
        using var connection = new LdapConnection(
            new LdapDirectoryIdentifier(string.IsNullOrEmpty(_options.LdapServer) ? null : _options.LdapServer));
        connection.SessionOptions.ProtocolVersion = 3;
        connection.AuthType = AuthType.Negotiate;
        var timeout = TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds));
        connection.Timeout = timeout;
        connection.Bind();

        // IsNullOrEmpty, not ??: appsettings binds an empty string, which must also fall
        // back to the RootDSE lookup — an empty base DN searches nothing (NO_OBJECT).
        if (string.IsNullOrEmpty(searchBase))
            searchBase = string.IsNullOrEmpty(_options.DefaultSearchBase)
                ? ReadDefaultNamingContext(connection)
                : _options.DefaultSearchBase;

        var results = new List<AdComputer>();
        var pageControl = new PageResultRequestControl(Math.Min(_options.PageSize, maxResults));

        while (true)
        {
            var request = new SearchRequest(searchBase, ldapFilter, SearchScope.Subtree, Attributes);
            request.Controls.Add(pageControl);

            var response = (SearchResponse)connection.SendRequest(request);
            foreach (SearchResultEntry entry in response.Entries)
            {
                var cn = GetAttribute(entry, "cn");
                if (cn is null)
                    continue;

                results.Add(new AdComputer(
                    Name: cn,
                    DnsHostName: GetAttribute(entry, "dNSHostName"),
                    OperatingSystem: GetAttribute(entry, "operatingSystem"),
                    DistinguishedName: entry.DistinguishedName));

                if (results.Count >= maxResults)
                    return results;
            }

            var pageResponse = response.Controls.OfType<PageResultResponseControl>().FirstOrDefault();
            if (pageResponse is null || pageResponse.Cookie.Length == 0)
                return results;

            pageControl.Cookie = pageResponse.Cookie;
        }
    }

    private static string ReadDefaultNamingContext(LdapConnection connection)
    {
        var request = new SearchRequest(null, "(objectClass=*)", SearchScope.Base, "defaultNamingContext");
        var response = (SearchResponse)connection.SendRequest(request);
        var value = response.Entries.Count > 0 ? GetAttribute(response.Entries[0], "defaultNamingContext") : null;
        return value ?? throw new InvalidOperationException(
            "Could not determine the AD default naming context; set Directory:DefaultSearchBase.");
    }

    private static string? GetAttribute(SearchResultEntry entry, string name)
        => entry.Attributes[name] is { Count: > 0 } attribute ? attribute[0]?.ToString() : null;
}
