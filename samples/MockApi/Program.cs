var builder = WebApplication.CreateBuilder(args);

// 内置 OpenAPI：暴露标准 OpenAPI 3.x 文档到 /openapi/v1.json，供 HttpClientGen 解析生成客户端
builder.Services.AddOpenApi();

var app = builder.Build();

app.MapOpenApi();

// ---- 内存数据源，模拟后端接口 ----
var pets = new List<PetDto>
{
    new(1, "Rex", "dog", "available", DateTimeOffset.UtcNow),
    new(2, "Kitty", "cat", "sold", DateTimeOffset.UtcNow),
};
var orders = new List<OrderDto>
{
    new(1, 1, 2, DateTimeOffset.UtcNow, 12.5),
};

// Pet 枚举字符串值（对应生成客户端枚举 PetStatus 的管线值 available/pending/sold）
const string Available = "available";

app.MapGet("/pets", (string? status, int? limit) =>
{
    var list = pets.AsEnumerable();
    if (!string.IsNullOrEmpty(status))
        list = list.Where(p => p.Status == status);
    var result = list.ToList();
    if (limit is > 0)
        result = result.Take(limit.Value).ToList();
    return Results.Ok(result);
}).Produces<List<PetDto>>().WithTags("Pets").WithName("listPets");

// 数组型查询参数：验证生成客户端将数组展开为重复键 tag=v1&tag=v2
app.MapGet("/pets/search", (string[]? tag) =>
{
    var list = tag is null || tag.Length == 0
        ? pets
        : pets.Where(p => p.Tag is not null && tag.Contains(p.Tag));
    return Results.Ok(list.ToList());
}).Produces<List<PetDto>>().WithTags("Pets").WithName("searchPets");

app.MapPost("/pets", (NewPetDto dto) =>
{
    var pet = new PetDto(pets.Max(p => p.Id) + 1, dto.Name, dto.Tag, Available, DateTimeOffset.UtcNow);
    pets.Add(pet);
    return Results.Created($"/pets/{pet.Id}", pet);
}).Produces<PetDto>(StatusCodes.Status201Created).WithTags("Pets").WithName("createPet");

app.MapGet("/pets/{petId:long}", (long petId) =>
{
    var pet = pets.FirstOrDefault(p => p.Id == petId);
    return pet is null ? Results.NotFound() : Results.Ok(pet);
}).Produces<PetDto>().Produces(StatusCodes.Status404NotFound).WithTags("Pets").WithName("getPetById");

app.MapDelete("/pets/{petId:long}", (long petId) =>
{
    var removed = pets.RemoveAll(p => p.Id == petId);
    return removed > 0 ? Results.StatusCode(StatusCodes.Status204NoContent) : Results.NotFound();
}).Produces(StatusCodes.Status204NoContent).Produces(StatusCodes.Status404NotFound).WithTags("Pets").WithName("deletePet");

app.MapGet("/stores/orders", () => Results.Ok(orders)).Produces<List<OrderDto>>().WithTags("Stores").WithName("listOrders");

app.Run();

public partial class Program;

// ---------- mock 数据形状（JSON camelCase 与生成 DTO 字段一致，便于客户端反序列化） ----------

public sealed record PetDto(long Id, string Name, string? Tag, string Status, DateTimeOffset CreatedAt);
public sealed record NewPetDto(string Name, string? Tag);
public sealed record OrderDto(long Id, long PetId, int Quantity, DateTimeOffset ShipDate, double Price);