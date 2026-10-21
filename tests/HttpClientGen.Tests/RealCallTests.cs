using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HttpClientGen.Tests;

/// <summary>
/// 真实端到端测试：真实启动 MockApi → 从 /openapi/v1.json 用 HttpClientGen CLI 生成客户端
/// → 编译装载 → 对真实服务发起 HTTP 调用。整条链路与用户真实使用路径一致。
/// </summary>
[TestClass]
public sealed class RealCallTests
{
    private const string RootNs = "E2e.Client";

    private static HttpClient NewHttpClient(MockApiProcess server)
        => new() { BaseAddress = server.BaseAddress };

    [TestMethod]
    public async Task ListPets_真实GET_返回内存数据()
    {
        using var server = MockApiProcess.Start();
        using var gen = GeneratedClientLoader.LoadFromUrl(
            new Uri(server.BaseAddress, "openapi/v1.json").ToString(), RootNs);

        using var http = NewHttpClient(server);
        var client = gen.CreateClient($"{RootNs}.Pets.PetsClient", http);

        var pets = (object[])(await GeneratedClientLoader.InvokeAsync(client, "ListPets", null, null, CancellationToken.None))!;

        Assert.AreEqual(2, pets.Length);
        Assert.AreEqual(1L, GeneratedClientLoader.Prop(pets[0], "Id"));
        Assert.AreEqual("Rex", GeneratedClientLoader.Prop(pets[0], "Name"));
        Assert.AreEqual("available", GeneratedClientLoader.Prop(pets[0], "Status") as string);
    }

    [TestMethod]
    public async Task ListPets_带条件查询_过滤生效()
    {
        using var server = MockApiProcess.Start();
        using var gen = GeneratedClientLoader.LoadFromUrl(
            new Uri(server.BaseAddress, "openapi/v1.json").ToString(), RootNs);
        using var http = NewHttpClient(server);
        var client = gen.CreateClient($"{RootNs}.Pets.PetsClient", http);

        var sold = (object[])(await GeneratedClientLoader.InvokeAsync(client, "ListPets", "sold", null, CancellationToken.None))!;
        Assert.AreEqual(1, sold.Length);
        Assert.AreEqual(2L, GeneratedClientLoader.Prop(sold[0], "Id"));
        Assert.AreEqual("sold", GeneratedClientLoader.Prop(sold[0], "Status") as string);

        var none = (object[])(await GeneratedClientLoader.InvokeAsync(client, "ListPets", "pending", null, CancellationToken.None))!;
        Assert.AreEqual(0, none.Length);
    }

    [TestMethod]
    public async Task GetPetById_命中返回_未命中抛异常()
    {
        using var server = MockApiProcess.Start();
        using var gen = GeneratedClientLoader.LoadFromUrl(
            new Uri(server.BaseAddress, "openapi/v1.json").ToString(), RootNs);
        using var http = NewHttpClient(server);
        var client = gen.CreateClient($"{RootNs}.Pets.PetsClient", http);

        var kitty = await GeneratedClientLoader.InvokeAsync(client, "GetPetById", 2L, CancellationToken.None);
        Assert.IsNotNull(kitty);
        Assert.AreEqual("Kitty", GeneratedClientLoader.Prop(kitty!, "Name"));

        await Assert.ThrowsExceptionAsync<HttpRequestException>(() =>
            GeneratedClientLoader.InvokeAsync(client, "GetPetById", 999L, CancellationToken.None));
    }

    [TestMethod]
    public async Task CreatePet_真实POST_随即GET可读到()
    {
        using var server = MockApiProcess.Start();
        using var gen = GeneratedClientLoader.LoadFromUrl(
            new Uri(server.BaseAddress, "openapi/v1.json").ToString(), RootNs);
        using var http = NewHttpClient(server);
        var client = gen.CreateClient($"{RootNs}.Pets.PetsClient", http);

        var newPet = gen.CreateDto($"{RootNs}.Pets.NewPetDto", dto =>
        {
            dto.GetType().GetProperty("Name")!.SetValue(dto, "Buddy");
            dto.GetType().GetProperty("Tag")!.SetValue(dto, "dog");
        });

        var created = await GeneratedClientLoader.InvokeAsync(client, "CreatePet", newPet, CancellationToken.None);
        Assert.AreEqual(3L, GeneratedClientLoader.Prop(created!, "Id"));
        Assert.AreEqual("Buddy", GeneratedClientLoader.Prop(created!, "Name"));

        var fetched = await GeneratedClientLoader.InvokeAsync(client, "GetPetById", 3L, CancellationToken.None);
        Assert.AreEqual("Buddy", GeneratedClientLoader.Prop(fetched!, "Name"));
    }

    [TestMethod]
    public async Task DeletePet_真实DELETE_再次读取抛异常()
    {
        using var server = MockApiProcess.Start();
        using var gen = GeneratedClientLoader.LoadFromUrl(
            new Uri(server.BaseAddress, "openapi/v1.json").ToString(), RootNs);
        using var http = NewHttpClient(server);
        var client = gen.CreateClient($"{RootNs}.Pets.PetsClient", http);

        await GeneratedClientLoader.InvokeAsync(client, "DeletePet", 1L, CancellationToken.None);

        await Assert.ThrowsExceptionAsync<HttpRequestException>(() =>
            GeneratedClientLoader.InvokeAsync(client, "GetPetById", 1L, CancellationToken.None));
    }

    [TestMethod]
    public async Task Stores客户端_真实调用_返回订单()
    {
        using var server = MockApiProcess.Start();
        using var gen = GeneratedClientLoader.LoadFromUrl(
            new Uri(server.BaseAddress, "openapi/v1.json").ToString(), RootNs);
        using var http = NewHttpClient(server);
        var client = gen.CreateClient($"{RootNs}.Stores.StoresClient", http);

        var orders = (object[])(await GeneratedClientLoader.InvokeAsync(client, "ListOrders", CancellationToken.None))!;

        Assert.AreEqual(1, orders.Length);
        Assert.AreEqual(1L, GeneratedClientLoader.Prop(orders[0], "Id"));
        Assert.AreEqual(2, GeneratedClientLoader.Prop(orders[0], "Quantity"));
    }

    [TestMethod]
    public async Task SearchPets_数组查询_展开为重复键命中()
    {
        using var server = MockApiProcess.Start();
        using var gen = GeneratedClientLoader.LoadFromUrl(
            new Uri(server.BaseAddress, "openapi/v1.json").ToString(), RootNs);
        using var http = NewHttpClient(server);
        var client = gen.CreateClient($"{RootNs}.Pets.PetsClient", http);

        // 单元素数组 → ?tag=dog → 仅命中 Rex
        var found = (object[])(await GeneratedClientLoader.InvokeAsync(client, "SearchPets", new[] { "dog" }, CancellationToken.None))!;
        Assert.AreEqual(1, found.Length);
        Assert.AreEqual("Rex", GeneratedClientLoader.Prop(found[0], "Name"));

        // 空数组 → 不拼接查询→返回全部 2 条
        var all = (object[])(await GeneratedClientLoader.InvokeAsync(client, "SearchPets", Array.Empty<string>(), CancellationToken.None))!;
        Assert.AreEqual(2, all.Length);
    }
}