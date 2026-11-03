using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Fluid.Ast;
using Fluid.Parser;
using Fluid.SourceGeneration;
using Fluid.Tests.Mocks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace Fluid.Tests;

public class SourceGenerationTests
{
    [Theory]
    [InlineData("Hello", "Hello")]
    [InlineData("{{ 1 }}", "1")]
    [InlineData("{{ '' }}{{ 'a' }}{{ 'hello' }}", "ahello")]
    [InlineData("{% assign x = 1 %}{{ x }}", "1")]
    [InlineData("{% if true %}a{% else %}b{% endif %}", "a")]
    [InlineData("{% unless false %}a{% endunless %}", "a")]
    [InlineData("{% unless true %}a{% elsif true %}b{% else %}c{% endunless %}", "b")]
    [InlineData("{% for i in (1..3) %}{{ i }}{% endfor %}", "123")]
    [InlineData("{% for i in (1..3) %}{% if i == 2 %}{% break %}{% endif %}{{ i }}{% endfor %}", "1")]
    [InlineData("{% capture x %}hi{% endcapture %}{{ x }}", "hi")]
    [InlineData("{% cycle 'a','b' %}{% cycle 'a','b' %}{% cycle 'a','b' %}", "aba")]
    [InlineData("{% increment x %}{% increment x %}{% decrement x %}", "011")]
    [InlineData("{% increment x %}{{ x }}", "01")]
    [InlineData("{% case 2 %}{% when 1 %}a{% when 2 %}b{% else %}c{% endcase %}", "b")]
    [InlineData("{% if 2 > 1 and 2 >= 2 and 1 < 2 and 2 <= 2 %}yes{% endif %}", "yes")]
    [InlineData("{% if '' == empty and '' == blank %}yes{% endif %}", "yes")]
    [InlineData("{% if 'x' contains nil %}yes{% else %}no{% endif %}", "no")]
    [InlineData("{{ 'a' == 'b' }}|{{ 'b' != 'a' }}|{{ 'abc' contains 'b' }}|{{ 'abc' startswith 'a' }}|{{ 'abc' endswith 'c' }}", "a|b|abc|abc|abc")]
    [InlineData("{% capture x %}&{% endcapture %}{{ x }}", "&")]
    [InlineData("{{ 'a' | json }}", "&quot;a&quot;")]
    [InlineData("{% macro hello() %}hi{% endmacro %}{{ hello() }}", "hi")]
    public async Task GeneratedTemplate_MatchesRuntime(string liquid, string expected)
    {
        var parser = new FluidParser(new FluidParserOptions { AllowFunctions = true });
        var template = parser.Parse(liquid);

        var source = template.Compile(new SourceGenerationOptions
        {
            Namespace = "Fluid.Tests.Generated",
            ClassName = "T" + Guid.NewGuid().ToString("N")
        });

        var generated = CompileToAssembly(source.SourceCode);
        var type = generated.GetType(source.FullTypeName, throwOnError: true);

        var instance = (IFluidTemplate)Activator.CreateInstance(type, nonPublic: true);

        var runtimeWriter = new StringWriter();
        await template.RenderAsync(runtimeWriter, HtmlEncoder.Default, new TemplateContext());

        var generatedWriter = new StringWriter();
        await instance.RenderAsync(generatedWriter, HtmlEncoder.Default, new TemplateContext());

        Assert.Equal(expected, runtimeWriter.ToString());
        Assert.Equal(expected, generatedWriter.ToString());
    }

    [Fact]
    public async Task ForLoopOffsetsAreRelativeToTheSourceAndStayInBounds()
    {
#if COMPILED
        var parser = new FluidParser().Compile();
#else
        var parser = new FluidParser();
#endif
        var liquid =
            "{% assign stringOffset = '-1' %}{% assign computedOffset = -1 %}" +
            "{% assign lastArrayItem = items[-1] %}{{ lastArrayItem }}|" +
            "{% for a in (1..3) offset: -1 %}{{ a }}{% else %}none{% endfor %}|" +
            "{% for b in (1..3) offset: '-1' %}{{ b }}{% else %}none{% endfor %}|" +
            "{% for c in (1..3) offset: stringOffset %}{{ c }}{% else %}none{% endfor %}|" +
            "{% for d in (1..3) offset: computedOffset %}{{ d }}{% else %}none{% endfor %}|" +
            "{% for arrayItem in items offset: -1 %}{{ arrayItem }}{% else %}none{% endfor %}|" +
            "{% for e in (1..3) offset: -3 %}{{ e }}{% else %}none{% endfor %}|" +
            "{% for f in (1..3) offset: -4 %}{{ f }}{% else %}none{% endfor %}|" +
            "{% for g in (1..3) offset: -2147483648 %}{{ g }}{% else %}none{% endfor %}|" +
            "{% for h in (1..3) offset: 2147483647 %}{{ h }}{% else %}none{% endfor %}|" +
            "{% for i in (1..3) offset: -2 reversed %}{{ i }}{% else %}none{% endfor %}|" +
            "{% for j in (1..5) offset: -3 limit: -1 %}{{ j }}{% else %}none{% endfor %}|" +
            "{% for k in (1..3) offset: -1 limit: 0 %}{{ k }}{% else %}none{% endfor %}|" +
            "{% for l in emptyItems offset: -1 %}{{ l }}{% else %}none{% endfor %}|" +
            "{% for m in items offset: 1 %}{{ m }}{% else %}none{% endfor %}|" +
            "{% for n in items offset: -2 limit: 1 %}{{ n }}{% endfor %}|" +
            "{% for n in items offset: continue %}{{ n }}{% else %}none{% endfor %}";
        var sourceValues = new[] { 1, 2, 3 };
        var fiveSourceValues = new[] { 1, 2, 3, 4, 5 };
        var expectedLast = string.Join("", sourceValues.Skip(sourceValues.Length - 1));
        var expectedAll = string.Join("", sourceValues);
        var expectedReversedSuffix = string.Join("", sourceValues.Skip(sourceValues.Length - 2).Reverse());
        var expectedLimitedSuffix = string.Join("", fiveSourceValues.Skip(fiveSourceValues.Length - 3).Take(2));
        var expectedOffset = string.Join("", sourceValues.Skip(1));
        var expectedBeforeContinue = string.Join("", sourceValues.Skip(sourceValues.Length - 2).Take(1));
        var expectedContinue = string.Join("", sourceValues.Skip(sourceValues.Length - 1));
        var expected = string.Join("|", new[]
        {
            expectedLast, expectedLast, expectedLast, expectedLast, expectedLast, expectedLast,
            expectedAll, "none", "none", "none", expectedReversedSuffix, expectedLimitedSuffix,
            "none", "none", expectedOffset, expectedBeforeContinue, expectedContinue
        });
        var template = parser.Parse(liquid);
        var source = template.Compile(new SourceGenerationOptions
        {
            Namespace = "Fluid.Tests.Generated",
            ClassName = "T" + Guid.NewGuid().ToString("N")
        });
        var generated = CompileToAssembly(source.SourceCode);
        var type = generated.GetType(source.FullTypeName, throwOnError: true);
        var instance = (IFluidTemplate)Activator.CreateInstance(type, nonPublic: true);
        var runtimeWriter = new StringWriter();
        var generatedWriter = new StringWriter();
        var runtimeContext = new TemplateContext()
            .SetValue("items", sourceValues)
            .SetValue("emptyItems", Array.Empty<int>());
        var generatedContext = new TemplateContext()
            .SetValue("items", sourceValues)
            .SetValue("emptyItems", Array.Empty<int>());

        var runtimeFailure = await Record.ExceptionAsync(
            () => template.RenderAsync(runtimeWriter, HtmlEncoder.Default, runtimeContext).AsTask());
        var generatedFailure = await Record.ExceptionAsync(
            () => instance.RenderAsync(generatedWriter, HtmlEncoder.Default, generatedContext).AsTask());
        var runtimeOutput = runtimeWriter.ToString();
        var generatedOutput = generatedWriter.ToString();
        var renderedAsExpected = runtimeFailure is null && generatedFailure is null &&
            runtimeOutput == expected && generatedOutput == expected;
        var failureDetails = $"interpreter exception: {runtimeFailure?.GetType().Name ?? "none"}; " +
            $"interpreter output: '{runtimeOutput}'; generated exception: {generatedFailure?.GetType().Name ?? "none"}; " +
            $"generated output: '{generatedOutput}'";

        Assert.True(renderedAsExpected,
            "For-loop offsets must render the expected source-end slices in interpreted and generated templates. " + failureDetails);
    }

    [Fact]
    public async Task GeneratedTemplate_StrictVariables_ThrowsOnMissingVariable()
    {
        var parser = new FluidParser();
        var template = parser.Parse("{{ missing }}");

        var source = template.Compile(new SourceGenerationOptions
        {
            Namespace = "Fluid.Tests.Generated",
            ClassName = "T" + Guid.NewGuid().ToString("N")
        });

        var generated = CompileToAssembly(source.SourceCode);
        var type = generated.GetType(source.FullTypeName, throwOnError: true);
        var instance = (IFluidTemplate)Activator.CreateInstance(type, nonPublic: true);
        var context = new TemplateContext(new TemplateOptionsBuilder().WithStrictVariables(true).Build());

        var ex = await Assert.ThrowsAsync<FluidException>(() => instance.RenderAsync(new StringWriter(), HtmlEncoder.Default, context).AsTask());

        Assert.Contains("missing", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeneratedTemplate_FlushesOutput()
    {
        var parser = new FluidParser();
        var template = parser.Parse("Hello");
        var source = template.Compile(new SourceGenerationOptions
        {
            Namespace = "Fluid.Tests.Generated",
            ClassName = "T" + Guid.NewGuid().ToString("N")
        });

        var generated = CompileToAssembly(source.SourceCode);
        var type = generated.GetType(source.FullTypeName, throwOnError: true);
        var instance = (IFluidTemplate)Activator.CreateInstance(type, nonPublic: true);
        var output = new FlushTrackingOutput();

        await instance.RenderAsync(output, HtmlEncoder.Default, new TemplateContext());

        Assert.True(output.Flushed);
    }

    [Fact]
    public async Task GeneratedTemplate_EnforcesMaxOutputSize()
    {
        var parser = new FluidParser();
        var template = parser.Parse("12345");
        var source = template.Compile(new SourceGenerationOptions
        {
            Namespace = "Fluid.Tests.Generated",
            ClassName = "T" + Guid.NewGuid().ToString("N")
        });

        var generated = CompileToAssembly(source.SourceCode);
        var type = generated.GetType(source.FullTypeName, throwOnError: true);
        var instance = (IFluidTemplate)Activator.CreateInstance(type, nonPublic: true);
        var context = new TemplateContext { MaxOutputSize = 4 };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => instance.RenderAsync(new FlushTrackingOutput(), HtmlEncoder.Default, context).AsTask());
    }

    [Fact]
    public async Task GeneratedTemplate_RejectsTrimming()
    {
        var parser = new FluidParser();
        var template = parser.Parse("Hello {{ name }}");
        var source = template.Compile(new SourceGenerationOptions
        {
            Namespace = "Fluid.Tests.Generated",
            ClassName = "T" + Guid.NewGuid().ToString("N")
        });

        var generated = CompileToAssembly(source.SourceCode);
        var type = generated.GetType(source.FullTypeName, throwOnError: true);
        var instance = (IFluidTemplate)Activator.CreateInstance(type, nonPublic: true);
        var context = new TemplateContext(new TemplateOptionsBuilder().WithTrimming(TrimmingFlags.OutputLeft).Build());

        var ex = await Assert.ThrowsAsync<NotSupportedException>(() => instance.RenderAsync(new FlushTrackingOutput(), HtmlEncoder.Default, context).AsTask());

        Assert.Contains("Trimming", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_Fails_WhenStatementNotSourceable()
    {
        var template = new FluidTemplate(new CustomStatement());

        var ex = Assert.Throws<SourceGenerationException>(() => template.Compile(new SourceGenerationOptions
        {
            Namespace = "Fluid.Tests.Generated",
            ClassName = "Incompatible"
        }));

        Assert.Contains(nameof(CustomStatement), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Render_Compiles_Subtemplate_FromFileProvider()
    {
        var provider = new MockFileProvider()
            .Add("partial", "hi");

        var parser = new FluidParser();
        var template = parser.Parse("{% render 'partial' %}");

        var source = template.Compile(new SourceGenerationOptions
        {
            Namespace = "Fluid.Tests.Generated",
            ClassName = "T" + Guid.NewGuid().ToString("N"),
            FileProvider = provider
        });

        var generated = CompileToAssembly(source.SourceCode);
        var type = generated.GetType(source.FullTypeName, throwOnError: true);
        var instance = (IFluidTemplate)Activator.CreateInstance(type, nonPublic: true);

        var runtimeContext = new TemplateContext(new TemplateOptionsBuilder().WithFileProvider(provider).Build());

        var runtimeWriter = new StringWriter();
        await template.RenderAsync(runtimeWriter, HtmlEncoder.Default, runtimeContext);

        var generatedWriter = new StringWriter();
        await instance.RenderAsync(generatedWriter, HtmlEncoder.Default, new TemplateContext());

        Assert.Equal("hi", runtimeWriter.ToString());
        Assert.Equal("hi", generatedWriter.ToString());
    }

    [Fact]
    public async Task GeneratedRender_UsesContextRootWithoutCallerLocals()
    {
        var provider = new MockFileProvider()
            .Add("partial", "{{ input }}|{{ outer }}|{{ global }}");

        var parser = new FluidParser();
        var template = parser.Parse("{% assign outer = 'hidden' %}{% render 'partial' %}");
        var source = template.Compile(new SourceGenerationOptions
        {
            Namespace = "Fluid.Tests.Generated",
            ClassName = "T" + Guid.NewGuid().ToString("N"),
            FileProvider = provider
        });

        var generated = CompileToAssembly(source.SourceCode);
        var type = generated.GetType(source.FullTypeName, throwOnError: true);
        var instance = (IFluidTemplate)Activator.CreateInstance(type, nonPublic: true);

        var options = new TemplateOptionsBuilder()
            .WithFileProvider(provider)
            .WithGlobalValue("global", Fluid.Values.StringValue.Create("global"))
            .Build();

        var runtimeContext = new TemplateContext(options).SetValue("input", "root");
        var runtimeWriter = new StringWriter();
        using (runtimeContext.EnterScope())
        {
            await template.RenderAsync(runtimeWriter, HtmlEncoder.Default, runtimeContext);
        }

        var generatedContext = new TemplateContext(options).SetValue("input", "root");
        var generatedWriter = new StringWriter();
        using (generatedContext.EnterScope())
        {
            await instance.RenderAsync(generatedWriter, HtmlEncoder.Default, generatedContext);
        }

        Assert.Equal("root||global", runtimeWriter.ToString());
        Assert.Equal(runtimeWriter.ToString(), generatedWriter.ToString());
    }

    private static Assembly CompileToAssembly(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Latest));

        var references = new List<MetadataReference>();

        // Use TPA (trusted platform assemblies) for framework references.
        var tpa = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        foreach (var path in tpa.Split(Path.PathSeparator))
        {
            references.Add(MetadataReference.CreateFromFile(path));
        }

        // Add Fluid assembly explicitly.
        references.Add(MetadataReference.CreateFromFile(typeof(FluidParser).Assembly.Location));

        var compilation = CSharpCompilation.Create(
            assemblyName: "Fluid.Generated." + Guid.NewGuid().ToString("N"),
            syntaxTrees: new[] { syntaxTree },
            references: references.DistinctBy(r => ((PortableExecutableReference)r).FilePath),
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));

        using var peStream = new MemoryStream();
        var emit = compilation.Emit(peStream);

        if (!emit.Success)
        {
            var diagnostics = string.Join(Environment.NewLine, emit.Diagnostics.Select(d => d.ToString()));
            throw new InvalidOperationException(diagnostics + Environment.NewLine + source);
        }

        peStream.Position = 0;
        return AssemblyLoadContext.Default.LoadFromStream(peStream);
    }

    private sealed class CustomStatement : Statement
    {
        public override ValueTask<Completion> WriteToAsync(IFluidOutput writer, TextEncoder encoder, TemplateContext context)
        {
            return Normal();
        }
    }

    private sealed class FlushTrackingOutput : IFluidOutput
    {
        private char[] _buffer = new char[256];
        private int _index;

        public bool Flushed { get; private set; }

        public void Advance(int count) => _index += count;

        public Memory<char> GetMemory(int sizeHint = 0) => _buffer.AsMemory(_index);

        public Span<char> GetSpan(int sizeHint = 0) => _buffer.AsSpan(_index);

        public void Write(string value)
        {
            value.CopyTo(0, _buffer, _index, value.Length);
            _index += value.Length;
        }

        public void Write(char[] buffer, int index, int count)
        {
            buffer.AsSpan(index, count).CopyTo(_buffer.AsSpan(_index));
            _index += count;
        }

        public ValueTask FlushAsync()
        {
            Flushed = true;
            return default;
        }
    }
}
