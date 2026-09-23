using Microsoft.Extensions.DependencyInjection;
using Noof.Ledger.Application.Jobs;

namespace Noof.Ledger.Host.Tests;

internal sealed class ThrowingScopeFactory : IServiceScopeFactory
{
    public IServiceScope CreateScope() => new ThrowingScope();

    sealed class ThrowingScope : IServiceScope
    {
        public IServiceProvider ServiceProvider { get; } = new ThrowingProvider();
        public void Dispose() { }
    }

    sealed class ThrowingProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IJobQueue)
                ? throw new InvalidOperationException("the container cannot resolve IJobQueue")
                : null;
    }
}
