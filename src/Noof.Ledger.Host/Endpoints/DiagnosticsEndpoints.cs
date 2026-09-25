namespace Noof.Ledger.Host.Endpoints;

internal static class DiagnosticsEndpoints
{
    // A plain <form method="get"> cannot template a route's path segment from user input, so
    // /diagnostics's transaction-id lookup posts here instead of turning the whole static page
    // interactive for one field. Left to the global fallback authorization policy in Program.cs,
    // the same as every other endpoint that declares no [AllowAnonymous].
    public static void MapDiagnosticsEndpoints(this IEndpointRouteBuilder routes) =>
        routes.MapGet("/diagnostics/open-trace", (Guid id) => Results.Redirect($"/transactions/{id}/trace"));
}
