using Microsoft.AspNetCore.Http.HttpResults;

namespace SampleDiscoveryCatalog;

public static class CatalogEndpoints
{
    public static IServiceCollection AddDiscoveryCatalog(this IServiceCollection services, IDiscoveryCatalog catalog)
        => services.AddSingleton(catalog).AddProblemDetails().AddOpenApi();

    public static void MapDiscoveryCatalog(this WebApplication app)
    {
        app.MapGet("/catalog", Results<Ok<CatalogResponse>, StatusCodeHttpResult, ContentHttpResult> (IDiscoveryCatalog catalog, CancellationToken cancellationToken) =>
        {
            var current = catalog.ReadCatalog();
            if (current.Mode == "unavailable") return TypedResults.StatusCode(503);
            if (current.Mode == "malformed") return TypedResults.Text("{", "application/json");
            return TypedResults.Ok(current.Catalog);
        }).WithSummary("Read the configured sample destinations.")
          .WithDescription("Synthetic modes can return HTTP 503 or intentionally malformed JSON for recovery checks.");
        app.MapGet("/control", (IDiscoveryCatalog catalog, CancellationToken cancellationToken) => TypedResults.Ok(catalog.ReadSelection()))
            .WithSummary("Read the current local catalog selection.");
        app.MapPut("/control", Results<NoContent, ValidationProblem> (CatalogSelectionRequest request, IDiscoveryCatalog catalog, CancellationToken cancellationToken) =>
        {
            if (!catalog.TryUpdate(request)) return TypedResults.ValidationProblem(new Dictionary<string, string[]>
            { ["selection"] = ["Choose healthy, unavailable, or malformed and up to eight unique configured backend identifiers."] });
            return TypedResults.NoContent();
        }).WithSummary("Change the local catalog selection or synthetic failure mode.")
          .WithDescription("Only startup-configured backend identifiers are accepted. An empty selection removes all discovered destinations.");
    }
}
