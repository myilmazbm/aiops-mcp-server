using AiOps.Mcp.Directory;

namespace AiOps.Mcp.Tests;

public class LdapQueryBuilderTests
{
    [Fact]
    public void Bare_name_becomes_a_contains_pattern()
    {
        var (searchBase, filter) = LdapQueryBuilder.Build("sql");

        Assert.Null(searchBase);
        Assert.Contains("(cn=*sql*)", filter);
        Assert.Contains("(dNSHostName=*sql*)", filter);
    }

    [Fact]
    public void Explicit_wildcards_are_kept_as_given()
    {
        var (_, filter) = LdapQueryBuilder.Build("SQL0*");

        Assert.Contains("(cn=SQL0*)", filter);
        Assert.DoesNotContain("*SQL0*", filter);
    }

    [Theory]
    [InlineData("OU=Databases,DC=contoso,DC=local")]
    [InlineData("ou=Databases,dc=contoso,dc=local")]
    [InlineData("  OU = Databases,DC=contoso,DC=local  ")]
    public void Distinguished_names_become_the_search_base(string input)
    {
        var (searchBase, filter) = LdapQueryBuilder.Build(input);

        Assert.Equal(input.Trim(), searchBase);
        Assert.Contains("(cn=*)", filter);
    }

    [Fact]
    public void Only_server_os_computer_accounts_are_targeted()
    {
        var (_, filter) = LdapQueryBuilder.Build("anything");

        Assert.Contains("(objectCategory=computer)", filter);
        Assert.Contains("(operatingSystem=*Server*)", filter);
    }

    [Fact]
    public void Domain_controllers_are_excluded_by_policy()
    {
        // SERVER_TRUST_ACCOUNT (8192) via the LDAP bitwise-AND matching rule, negated.
        var (_, filter) = LdapQueryBuilder.Build("anything");

        Assert.Contains("(!(userAccountControl:1.2.840.113556.1.4.803:=8192))", filter);
    }

    [Fact]
    public void Filter_injection_is_neutralized()
    {
        var (_, filter) = LdapQueryBuilder.Build("x)(objectClass=*");

        // Parentheses must arrive escaped so the value cannot terminate the clause.
        Assert.Contains(@"\29\28objectClass", filter.Replace("x", ""));
        Assert.DoesNotContain(")(objectClass=*)", filter);
    }

    [Theory]
    [InlineData(@"a\b", @"a\5cb")]
    [InlineData("a(b)c", @"a\28b\29c")]
    [InlineData("a\0b", @"a\00b")]
    [InlineData("keep*wildcard", "keep*wildcard")]
    [InlineData("türkçe-sunucu", "türkçe-sunucu")]
    public void Escaping_covers_rfc4515_specials_but_keeps_wildcards(string input, string expected)
        => Assert.Equal(expected, LdapQueryBuilder.EscapeFilterValue(input));

    [Fact]
    public void Read_only_domain_controllers_are_excluded_too()
    {
        // An RODC computer account does NOT carry SERVER_TRUST_ACCOUNT (8192): it is a
        // workstation trust account (4096) with PARTIAL_SECRETS_ACCOUNT (67108864) added.
        // Excluding 8192 alone therefore returns RODCs, and a domain controller in the result
        // is exactly what the out-of-scope policy forbids.
        var (_, filter) = LdapQueryBuilder.Build("SQL*");

        Assert.Contains("(!(userAccountControl:1.2.840.113556.1.4.803:=8192))", filter);
        Assert.Contains("(!(userAccountControl:1.2.840.113556.1.4.803:=67108864))", filter);
    }

    [Fact]
    public void Domain_controller_exclusion_applies_to_an_ou_scoped_search_as_well()
    {
        // The OU form takes a different branch; a DC inside the named OU must still be filtered.
        var (searchBase, filter) = LdapQueryBuilder.Build("OU=Domain Controllers,DC=contoso,DC=local");

        Assert.Equal("OU=Domain Controllers,DC=contoso,DC=local", searchBase);
        Assert.Contains("(!(userAccountControl:1.2.840.113556.1.4.803:=8192))", filter);
        Assert.Contains("(!(userAccountControl:1.2.840.113556.1.4.803:=67108864))", filter);
    }
}
