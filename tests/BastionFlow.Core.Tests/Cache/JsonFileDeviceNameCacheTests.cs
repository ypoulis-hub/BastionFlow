using BastionFlow.Core.Cache;
using FluentAssertions;
using Xunit;

namespace BastionFlow.Core.Tests.Cache;

public class JsonFileDeviceNameCacheTests : IDisposable
{
    private readonly string _tempDir;
    private readonly JsonFileDeviceNameCache _cache;
    private const string Tenant = "test-tenant-id";

    public JsonFileDeviceNameCacheTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "BastionFlowTests-" + Guid.NewGuid());
        _cache = new JsonFileDeviceNameCache(_tempDir);
    }

    [Fact]
    public async Task TryGet_returns_null_when_empty()
    {
        var got = await _cache.TryGetAsync(Tenant, "any-vm");
        got.Should().BeNull();
    }

    [Fact]
    public async Task Set_then_Get_roundtrips()
    {
        await _cache.SetAsync(Tenant, "vmsoft1srv", "HBGAZ-SRV-SFT1");
        var got = await _cache.TryGetAsync(Tenant, "vmsoft1srv");
        got.Should().Be("HBGAZ-SRV-SFT1");
    }

    [Fact]
    public async Task Set_overwrites_existing()
    {
        await _cache.SetAsync(Tenant, "vm1", "OLD");
        await _cache.SetAsync(Tenant, "vm1", "NEW");
        var got = await _cache.TryGetAsync(Tenant, "vm1");
        got.Should().Be("NEW");
    }

    [Fact]
    public async Task GetAll_returns_full_map()
    {
        await _cache.SetAsync(Tenant, "a", "A1");
        await _cache.SetAsync(Tenant, "b", "B1");
        var all = await _cache.GetAllAsync(Tenant);
        all.Should().HaveCount(2);
        all["a"].Should().Be("A1");
        all["b"].Should().Be("B1");
    }

    [Fact]
    public async Task Tenants_are_isolated()
    {
        await _cache.SetAsync("tenant-a", "vm1", "A");
        await _cache.SetAsync("tenant-b", "vm1", "B");
        (await _cache.TryGetAsync("tenant-a", "vm1")).Should().Be("A");
        (await _cache.TryGetAsync("tenant-b", "vm1")).Should().Be("B");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }
}
