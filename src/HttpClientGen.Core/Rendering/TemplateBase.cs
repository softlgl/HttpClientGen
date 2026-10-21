using System.Text;
using HttpClientGen.Core.Models;

namespace HttpClientGen.Core.Rendering;

/// <summary>
/// Razor 模板的基类。运行时编译出的模板类继承它；
/// 所有 Write* 方法直接将文本写入输出（不做任何转义），以便干净地生成 C# 源码。
/// </summary>
public abstract class TemplateBase
{
    private readonly StringBuilder _buffer = new();
    private readonly StringWriter _output;

    protected TemplateBase()
    {
        _output = new StringWriter(_buffer, Culture_Invariant);
    }

    private static readonly IFormatProvider Culture_Invariant = System.Globalization.CultureInfo.InvariantCulture;

    /// <summary>模板写入的输出流。</summary>
    protected TextWriter Output => _output;

    /// <summary>渲染完成的文本。</summary>
    public string Result => _buffer.ToString();

    public abstract Task ExecuteAsync();

    public virtual void WriteLiteral(string? value)
    {
        if (value is not null) _output.Write(value);
    }

    public virtual void WriteLiteralTo(TextWriter writer, string? value)
    {
        if (value is not null) writer.Write(value);
    }

    public virtual void Write(object? value)
    {
        if (value is not null) _output.Write(value);
    }

    public virtual void WriteTo(TextWriter writer, object? value)
    {
        if (value is not null) writer.Write(value);
    }

    public virtual void BeginWriteAttribute(string name, string prefix, int prefixOffset, string suffix, int suffixOffset, int attributeValuesCount)
    {
    }

    public virtual void EndWriteAttribute()
    {
    }

    public virtual void WriteAttributeValue(string prefix, int prefixOffset, object? value, int valueOffset, int valueLength, bool isLiteral)
    {
    }
}

/// <summary>带模型的 Razor 模板基类。</summary>
public abstract class TemplateBase<TModel> : TemplateBase
{
    public TModel? Model { get; set; }
}