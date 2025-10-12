using System;

sealed record EndpointAccessMetadata(bool RequiresSession)
{
    public static EndpointAccessMetadata Public { get; } = new(false);
    public static EndpointAccessMetadata Private { get; } = new(true);
}
