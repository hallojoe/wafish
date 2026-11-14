using Wafish.ReactUi.Host;

await using var fixture = await UiTestFixture.CreateAsync(UiTestFixtureOptions.FromEnvironment(), args);
await fixture.StartAsync();
await fixture.App.WaitForShutdownAsync();
