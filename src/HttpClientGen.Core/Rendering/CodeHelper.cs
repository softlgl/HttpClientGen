using HttpClientGen.Core.Models;

namespace HttpClientGen.Core.Rendering;

/// <summary>供模板调用的代码片段辅助方法（模板内避免使用 @functions/@using 等易碎的 Razor 指令）。</summary>
public static class CodeHelper
{
    public static string ModelType(ApiProperty p) => p.Type + (p.IsNullable ? "?" : "");

    public static string ParamType(ApiParameter p) => p.Type + (p.IsNullable ? "?" : "");

    public static string ReturnType(ApiOperation op) => op.HasResponse ? $"Task<{op.ResponseType}>" : "Task";

    public static string Params(ApiOperation op)
    {
        var parts = new List<string>();
        foreach (var p in op.PathParams) parts.Add($"{ParamType(p)} {p.Name}");
        if (op.Body is not null) parts.Add($"{ParamType(op.Body)} {op.Body.Name}");
        foreach (var p in op.QueryParams)
            parts.Add(p.IsRequired ? $"{ParamType(p)} {p.Name}" : $"{NullableType(ParamType(p))} {p.Name} = null");
        var s = string.Join(", ", parts);
        return (parts.Count > 0 ? s + ", " : "") + "CancellationToken ct = default";
    }

    /// <summary>确保值类型/引用类型都可空（为可选参数的 null 默认值服务）。</summary>
    private static string NullableType(string t)
        => t.EndsWith("?") || t.Contains('<') || t.Contains('[') ? t : t + "?";

    public static string PathPairs(ApiOperation op)
    {
        // 传原值（装箱为 object?）：标量就地转线值、数组按逗号拼接，均在运行时 FormatPathValue 处理。
        var items = op.PathParams.Select(p =>
            $"(\"{p.JsonName}\", {p.Name})");
        return string.Join(", ", items);
    }

    public static string QueryPairs(ApiOperation op)
    {
        // 传原值 + 序列化风格：null 跳过、数组按 style/explode 展开（重复键/逗号/空格/竖线）、枚举转线值，均运行时处理。
        var items = op.QueryParams.Select(p =>
            $"(\"{p.JsonName}\", {p.Name}, \"{p.Style}\", {(p.Explode ? "true" : "false")})");
        return string.Join(", ", items);
    }

    /// <summary>SendAsync 最后一个 json 形参；有 body 时序列化，否则传 null。</summary>
    public static string BodyArg(ApiOperation op)
        => op.HasBody ? $", System.Text.Json.JsonSerializer.Serialize({op.Body!.Name}, JsonOpts)" : ", null";

    /// <summary>生成 HttpClientFactory 的类型化客户端注册语句（避免在模板中出现裸 &lt;/&gt;）。</summary>
    public static string AddClient(ClientClass c)
        => $"AddHttpClient<I{c.Name}, {c.Name}>(http => {{ if (baseAddress is not null) http.BaseAddress = baseAddress; }});";
}