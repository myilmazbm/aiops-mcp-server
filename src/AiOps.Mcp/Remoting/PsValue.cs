using System.Collections;
using System.Management.Automation;

namespace AiOps.Mcp.Remoting;

/// <summary>Helpers for reading properties off (possibly deserialized) PSObjects.</summary>
public static class PsValue
{
    public static T? Get<T>(PSObject obj, string name)
    {
        var value = obj.Properties[name]?.Value;
        if (value is null)
            return default;

        // One malformed value (e.g. a locale-mangled date string) must not fail a whole
        // result set; inventory favors resilience over strictness.
        try
        {
            return LanguagePrimitives.ConvertTo<T>(value);
        }
        catch (PSInvalidCastException)
        {
            return default;
        }
    }

    /// <summary>Enumerates a collection-valued property (deserialized collections arrive as ArrayList).</summary>
    public static IEnumerable<PSObject> GetMany(PSObject obj, string name)
    {
        var value = obj.Properties[name]?.Value;
        if (value is null)
            yield break;

        if (LanguagePrimitives.GetEnumerable(value) is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                if (item is not null)
                    yield return PSObject.AsPSObject(item);
            }
        }
        else
        {
            yield return PSObject.AsPSObject(value);
        }
    }
}
