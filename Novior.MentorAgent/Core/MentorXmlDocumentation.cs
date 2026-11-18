using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Novior.MentorAgent.Core;

/// <summary>
/// Story 1569, slice B — <c>candidate.described-by-xml</c>: the <c>&lt;summary&gt;</c> of a method's XML documentation
/// comment, read from the <c>&lt;assembly&gt;.xml</c> file the build writes next to the assembly
/// (<c>GenerateDocumentationFile</c>), without its tags. The text is the application's and is a description like any
/// other: it is not read for instructions.
/// </summary>
internal static partial class MentorXmlDocumentation
{
    /// <summary>The documentation file next to <paramref name="assembly"/>; null for an assembly with no file on disk.</summary>
    internal static string? FileOf(Assembly assembly)
        => assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location) ? null : Path.ChangeExtension(assembly.Location, ".xml");

    /// <summary>
    /// The summaries of the file at <paramref name="path"/>, by member id: none when the file is missing, and none, with
    /// a warning, when it cannot be read.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> Summaries(string? path, ILogger logger)
    {
        if (path is null || !File.Exists(path)) return new Dictionary<string, string>();
        try
        {
            using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var members = XDocument.Load(reader).Root?.Element("members")?.Elements("member") ?? [];
            var summaries = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var member in members)
                if (member.Attribute("name")?.Value is { } id && member.Element("summary") is { } summary)
                    summaries[id] = Text(summary);
            return summaries;
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("[MentorAgent] AutoMentor: the documentation file {Path} could not be read ({Error}): its " +
                              "candidates have no description.", path, ex.Message);
            return new Dictionary<string, string>();
        }
    }

    /// <summary>The member id the compiler gives <paramref name="method"/>: <c>M:Namespace.Type.Method(ParameterTypes)</c>.</summary>
    internal static string IdOf(MethodInfo method)
    {
        var parameters = method.GetParameters();
        return "M:" + NameOf(method.DeclaringType!) + "." + method.Name
               + (parameters.Length == 0 ? "" : "(" + string.Join(",", parameters.Select(p => NameOf(p.ParameterType))) + ")");
    }

    private static string NameOf(Type type)
    {
        if (type.IsArray)
            return NameOf(type.GetElementType()!)
                   + (type.GetArrayRank() == 1 ? "[]" : "[" + string.Join(",", Enumerable.Repeat("0:", type.GetArrayRank())) + "]");
        if (type.IsGenericParameter)
            return (type.DeclaringMethod is null ? "`" : "``") + type.GenericParameterPosition;
        if (type.IsConstructedGenericType)
            return Arity().Replace(NameOf(type.GetGenericTypeDefinition()), "")
                   + "{" + string.Join(",", type.GetGenericArguments().Select(NameOf)) + "}";
        return (type.FullName ?? type.Name).Replace('+', '.');
    }

    /// <summary>The text of <paramref name="element"/>: a reference gives its name, and the white space is one space.</summary>
    internal static string Text(XElement element)
    {
        var text = new StringBuilder();
        Append(text, element);
        return Spaces().Replace(text.ToString(), " ").Trim();
    }

    private static void Append(StringBuilder text, XElement element)
    {
        foreach (var node in element.Nodes())
            switch (node)
            {
                case XText t:
                    text.Append(t.Value);
                    break;
                case XElement e when Blocks.Contains(e.Name.LocalName):
                    text.Append(' ');
                    Append(text, e);
                    text.Append(' ');
                    break;
                case XElement e when !e.Nodes().Any():
                    text.Append(Reference(e));
                    break;
                case XElement e:
                    Append(text, e);
                    break;
            }
    }

    /// <summary>The elements that stand apart from the text around them.</summary>
    private static readonly HashSet<string> Blocks = new(["para", "br", "code", "list", "listheader", "item", "term", "description"],
        StringComparer.Ordinal);

    /// <summary><c>&lt;see cref="T:A.B"/&gt;</c> gives <c>B</c>, <c>&lt;paramref name="x"/&gt;</c> gives <c>x</c>.</summary>
    private static string Reference(XElement element)
    {
        if (element.Attribute("langword")?.Value is { } word) return word;
        if (element.Attribute("name")?.Value is { } name) return name;
        if (element.Attribute("cref")?.Value is not { } cref) return element.Attribute("href")?.Value ?? "";

        var member = cref.Length > 2 && cref[1] == ':' ? cref[2..] : cref;
        var paren = member.IndexOf('(');
        if (paren >= 0) member = member[..paren];
        return Arity().Replace(member[(member.LastIndexOf('.') + 1)..], "");
    }

    [GeneratedRegex(@"`+\d+")]
    private static partial Regex Arity();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
