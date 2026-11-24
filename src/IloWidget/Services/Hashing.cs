namespace IloWidget.Services;

public static class Hashing
{
    /// <summary>FNV-1a; string.GetHashCode is randomized per process and cannot name a shared mutex or registry value.</summary>
    public static string Stable(string text)
    {
        var hash = 2166136261u;
        foreach (var ch in text)
            hash = (hash ^ ch) * 16777619u;
        return hash.ToString("x8");
    }
}
