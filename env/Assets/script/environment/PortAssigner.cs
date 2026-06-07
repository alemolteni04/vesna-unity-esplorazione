using System.Collections.Generic;
public static class PortAssigner
{
    private static readonly Dictionary<string, string> _assigned = new();
    private static int _next = 9000;

    public static string GetPort(string name)
    {
        if (!_assigned.ContainsKey(name))
            _assigned[name] = (_next++).ToString();
        return _assigned[name];
    }
}