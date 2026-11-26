namespace Wafish.ReactUi.Host;

internal sealed class UiTestFixtureOptions
{
    public required string Password { get; init; }
    public string? Root { get; init; }
    public int Port { get; init; } = 5219;
    public bool Https { get; init; }
    public string AuthMode { get; init; } = "DefaultJwt";
    public string ApiPrefix { get; init; } = "wafish";
    public string UiPrefix { get; init; } = "console";
    public string? PathBase { get; init; }
    public string Profile { get; init; } = "baseline";

    public static UiTestFixtureOptions FromEnvironment(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;
        var options = new UiTestFixtureOptions
        {
            Password = read("UI_TEST_PASSWORD") ?? "",
            Root = string.IsNullOrWhiteSpace(read("UI_TEST_ROOT")) ? null : read("UI_TEST_ROOT"),
            Port = int.Parse(read("UI_TEST_PORT") ?? "5219"),
            Https = bool.Parse(read("UI_TEST_HTTPS") ?? "false"),
            AuthMode = read("UI_TEST_AUTH") ?? "DefaultJwt",
            ApiPrefix = read("UI_TEST_API_PREFIX") ?? "wafish",
            UiPrefix = read("UI_TEST_UI_PREFIX") ?? "console",
            PathBase = read("UI_TEST_PATH_BASE"),
            Profile = read("UI_TEST_PROFILE") ?? "baseline"
        };
        options.Validate();
        return options;
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Password)) throw new InvalidOperationException("UI_TEST_PASSWORD is required for this test-only host.");
        if (Port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(Port));
        if (AuthMode is not ("DefaultJwt" or "BasicAuth" or "ApiKey")) throw new ArgumentException("Unknown UI_TEST_AUTH.");
        if (Profile is not ("baseline" or "large" or "plugins" or "storage-failure" or "version-conflict" or "delayed-failure" or "activation-failure" or "passkeys")) throw new ArgumentException("Unknown UI_TEST_PROFILE.");
        if (!string.IsNullOrEmpty(PathBase) && (!PathBase.StartsWith('/') || PathBase.EndsWith('/'))) throw new ArgumentException("UI_TEST_PATH_BASE requires a leading slash and no trailing slash.");
        if (string.IsNullOrWhiteSpace(ApiPrefix) || string.IsNullOrWhiteSpace(UiPrefix)) throw new ArgumentException("Fixture prefixes cannot be empty.");
    }
}
