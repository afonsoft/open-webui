using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;

namespace OpenWebUI.Api.Tests;

/// <summary>HybridCache real (in-process) para testes de serviços que recebem HybridCache.</summary>
internal static class TestCache
{
    public static HybridCache Create() =>
        new ServiceCollection().AddHybridCache().Services.BuildServiceProvider()
            .GetRequiredService<HybridCache>();
}
