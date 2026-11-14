using System.Text.Json;
using Google.Protobuf;
using Wafish.GatewayRegistry;
using Wafish.Services;

internal static class RegistrationWireChecks
{
    public static Task RunAsync(Func<string, Func<Task>, Task> run) => run(
        "Registration wire: legacy fields, additive extensions, optional presence, and round trips remain compatible", () =>
        {
            var root = Directory.GetCurrentDirectory();
            while (!File.Exists(Path.Combine(root, "Wafish.slnx"))) root = Directory.GetParent(root)!.FullName;
            using var fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "tests/contracts/grpc/wire-fixtures.json")));
            foreach (var fixture in fixtures.RootElement.EnumerateArray())
            {
                var name = fixture.GetProperty("name").GetString();
                var bytes = Convert.FromHexString(fixture.GetProperty("hex").GetString()!);
                IMessage message = fixture.GetProperty("type").GetString() switch
                {
                    nameof(RegisterServiceRequest) => RegisterServiceRequest.Parser.ParseFrom(bytes),
                    nameof(RegisterServiceResponse) => RegisterServiceResponse.Parser.ParseFrom(bytes),
                    nameof(UpdateDestinationsRequest) => UpdateDestinationsRequest.Parser.ParseFrom(bytes),
                    nameof(UpdateDestinationsResponse) => UpdateDestinationsResponse.Parser.ParseFrom(bytes),
                    nameof(HeartbeatRequest) => HeartbeatRequest.Parser.ParseFrom(bytes),
                    _ => throw new Exception("Unknown fixture message type.")
                };
                Check(message.ToByteArray().SequenceEqual(bytes), name + " changed known fields or discarded unknown fields.");
                switch (message)
                {
                    case RegisterServiceRequest request:
                        Check(request.ServiceId == "fixture" && request.InstanceId == "" && request.Paths.Single() == "/api"
                            && request.Destinations.Single().Address == "http://localhost:9000/" && request.Metadata["team"] == "fixture"
                            && request.LoadBalancing == LoadBalancingPolicy.RoundRobin, "Legacy registration request changed meaning.");
                        break;
                    case RegisterServiceResponse response:
                        Check(response.Success && response.RouteIds.Single() == "fixture" && response.RegisteredAt == 1700000000, "Legacy response fields changed.");
                        Check(response.HasActiveVersion == (name == "register-response-current"), "Absent revision became explicit zero.");
                        using (var oldReader = new CodedInputStream(bytes))
                        {
                            var success = false; var cluster = "";
                            uint tag;
                            while ((tag = oldReader.ReadTag()) != 0)
                            {
                                if (tag == 8) success = oldReader.ReadBool();
                                else if (tag == 26) cluster = oldReader.ReadString();
                                else oldReader.SkipLastField();
                            }
                            Check(success && cluster == "cluster", "Additive response fields broke a legacy reader.");
                        }
                        break;
                    case UpdateDestinationsRequest update:
                        Check(update.HasExpectedVersion && update.ExpectedVersion == 0 && update.InstanceId == "instance", "Explicit zero revision was lost.");
                        break;
                    case UpdateDestinationsResponse update:
                        Check(!update.Success && update.Status == "ActivationFailed" && update.PersistedVersion == 12 && update.ActiveVersion == 11,
                            "Activation outcome changed meaning.");
                        break;
                    case HeartbeatRequest heartbeat:
                        Check(heartbeat.HasDestinationId && heartbeat.DestinationId == "", "Optional empty string lost presence.");
                        break;
                }
            }
            return Task.CompletedTask;
        });
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
