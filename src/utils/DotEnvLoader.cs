using System;
using System.Collections.Generic;
using System.IO;

internal static class DotEnvLoader
{
    public static void TryLoad(string path)
    {
        if (!File.Exists(path))
            return;

        foreach (var (key, value) in Parse(File.ReadLines(path)))
        {
            if (string.IsNullOrEmpty(key) || value is null)
                continue;

            if (Environment.GetEnvironmentVariable(key) is null)
                Environment.SetEnvironmentVariable(key, value);
        }
    }

    private static IEnumerable<(string Key, string? Value)> Parse(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            var idx = line.IndexOf('=');
            if (idx <= 0)
                continue;

            var key = line[..idx].Trim();
            var value = line[(idx + 1)..].Trim();

            if (value.StartsWith('"') && value.EndsWith('"') && value.Length >= 2)
                value = value[1..^1];
            else if (value.StartsWith('\'') && value.EndsWith('\'') && value.Length >= 2)
                value = value[1..^1];

            yield return (key, value);
        }
    }
}
