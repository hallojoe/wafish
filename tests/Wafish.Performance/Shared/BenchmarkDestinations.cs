namespace Wafish.Performance;

internal sealed record BenchmarkDestinations(Uri[] Addresses, string Policy)
{
    public string Id(int index) => Addresses.Length == 1 ? "backend" : "backend-" + (index + 1);

    public static BenchmarkDestinations Read(string[] args, BenchmarkTransport transport)
    {
        var list = BenchmarkOptions.Read(args, "--backend-urls", "");
        if (list.Length > 0 && args.Any(a => a == "--backend-url" || a.StartsWith("--backend-url=", StringComparison.Ordinal)))
            throw new ArgumentException("Use either --backend-url or --backend-urls.");
        var values = list.Length == 0
            ? new[] { BenchmarkOptions.Read(args, "--backend-url", $"{transport.Scheme}://127.0.0.1:5300") }
            : list.Split(',', StringSplitOptions.TrimEntries);
        if (values.Length is < 1 or > 8) throw new ArgumentException("Supply 1..8 backend origins.");
        var addresses = values.Select(value => transport.Address(value, "--backend-urls")).ToArray();
        if (addresses.Any(a => a.Port == 0) || addresses.Select(a => a.Port).Distinct().Count() != addresses.Length)
            throw new ArgumentException("Backend listener ports must be distinct and nonzero.");
        var policy = BenchmarkOptions.Read(args, "--load-balancing", "PowerOfTwoChoices");
        if (policy is not ("PowerOfTwoChoices" or "RoundRobin" or "LeastRequests" or "Random" or "FirstAlphabetical"))
            throw new ArgumentException("Unknown load-balancing policy.");
        return new(addresses, policy);
    }
}
