using System.Collections.Generic;

internal interface IPlayerIdentityRequest
{
    string? PlayerId { get; }
    IEnumerable<string?> EnumeratePlayerShortIds();
}
