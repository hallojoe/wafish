namespace Wafish.Plugins;

/// <summary>Marks an authenticated visitor request. Contains no credentials or claims.</summary>
public sealed record GatewayVisitorIdentityFeature(string SiteId);
