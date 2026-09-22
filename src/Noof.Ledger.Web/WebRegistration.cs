using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace Noof.Ledger.Web;

[SuppressMessage("Maintainability", "CA1515",
    Justification = "The one public way into this assembly. MudBlazor's services are a UI concern "
        + "and registering them from the Host would make Program.cs name a component library.")]
public static class WebRegistration
{
    // AddRazorComponents and AddInteractiveServerComponents deliberately stay in the Host. They live
    // in the ASP.NET Core shared framework, which this assembly does not reference and must not:
    // Noof.Ledger.Web is a Razor class library precisely so that "UI only, no Program.cs" is a build
    // error rather than a convention. MudBlazor is the part that is genuinely this assembly's.
    public static IServiceCollection AddNoofWeb(this IServiceCollection services)
    {
        services.AddMudServices();

        return services;
    }
}
