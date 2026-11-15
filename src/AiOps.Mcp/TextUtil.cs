namespace AiOps.Mcp;

public static class TextUtil
{
    /// <summary>Length-capped cut that never splits a surrogate pair.</summary>
    public static string Truncate(string value, int maxLength)
    {
        if (value.Length <= maxLength)
            return value;

        var cut = char.IsHighSurrogate(value[maxLength - 1]) ? maxLength - 1 : maxLength;
        return value[..cut];
    }
}
