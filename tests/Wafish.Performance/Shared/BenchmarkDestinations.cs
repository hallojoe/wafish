namespace Wafish.Performance;

internal sealed record BenchmarkDestinations(Uri[] Addresses, string LoadBalancingPolicy)
{
    public static string Id(int index) => index == 0 ? "backend" : "backend-" + (index + 1);
    public static BenchmarkDestinations Read(string[] args, BenchmarkTransport transport)
    {
        bool Has(string name) => args.Any(arg => arg == name || arg.StartsWith(name + "=", StringComparison.Ordinal));
        if (Has("--backend-urls") && Has("--backend-url"))
            throw new ArgumentException("Use either --backend-url or --backend-urls.");
        var raw = BenchmarkOptions.Read(args, "--backend-urls", BenchmarkOptions.Read(args, "--backend-url", $"{transport.Scheme}://127.0.0.1:5300"));
        if (raw.Length > 4096) throw new ArgumentException("Backend origins exceed the 4096-character bound.");
        var values = raw.Split(',', StringSplitOptions.TrimEntries);
        if (values.Length is < 1 or > 8) throw new ArgumentException("Configure one to eight backend origins.");
        var addresses = values.Select(value => transport.Address(value, "--backend-urls")).ToArray();
        if (addresses.Any(address => address.Port == 0) || addresses.Select(address => address.Port).Distinct().Count() != addresses.Length)
            throw new ArgumentException("Backend ports must be distinct and nonzero.");
        var policy = BenchmarkOptions.Read(args, "--load-balancing-policy", "PowerOfTwoChoices");
        if (policy is not ("PowerOfTwoChoices" or "RoundRobin" or "Random" or "LeastRequests" or "FirstAlphabetical"))
            throw new ArgumentException("Unknown YARP load-balancing policy.");
        return new(addresses, policy);
    }
}
