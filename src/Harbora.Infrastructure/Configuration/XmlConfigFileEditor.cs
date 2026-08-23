using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using Harbora.Application.Abstractions;
using Harbora.Domain.Configuration;

namespace Harbora.Infrastructure.Configuration;

/// <summary>
/// <c>web.config</c>/<c>app.config</c> — classic .NET, still widespread. Key path is real XPath
/// (<c>connectionStrings/add[@name='Default']/@connectionString</c>), not an invented "XPath-ish"
/// syntax — .NET's own <see cref="System.Xml.XPath"/> engine already exists, already understands
/// attributes/predicates/positions properly, and a config file's own author already reasons about
/// its shape this way.
///
/// <para>
/// Parsed with <see cref="XDocument"/> using <see cref="LoadOptions.PreserveWhitespace"/>: every
/// untouched node — including comments, which <see cref="XComment"/> carries through automatically —
/// keeps its exact original text. <see cref="Apply"/> mutates only the one matched node's value via
/// its editable <see cref="XPathNavigator"/> and re-saves the whole document, so only that node's
/// text changes; nothing else in the file is reformatted.
/// </para>
/// </summary>
public sealed class XmlConfigFileEditor : IConfigFileEditor
{
    public ConfigFileFormat Format => ConfigFileFormat.Xml;

    public ConfigFileInspection Inspect(string content, string? keyPath)
    {
        XDocument doc;
        try { doc = Load(content); }
        catch (XmlException ex) { return ConfigFileInspection.ParseFailure(ToParseError(ex)); }

        var paths = ListPaths(doc);
        if (keyPath is null) return new ConfigFileInspection(true, null, paths, false, null);

        XPathNavigator? node;
        try { node = doc.CreateNavigator().SelectSingleNode(keyPath); }
        catch (XPathException ex) { return ConfigFileInspection.ParseFailure(new ConfigFileParseError($"'{keyPath}' is not a valid XPath expression: {ex.Message}", null, null)); }

        return node is null
            ? new ConfigFileInspection(true, null, paths, false, null)
            : new ConfigFileInspection(true, null, paths, true, node.Value);
    }

    public ConfigFileEditOutcome Apply(string content, string keyPath, string newValue)
    {
        XDocument doc;
        try { doc = Load(content); }
        catch (XmlException ex) { return ConfigFileEditOutcome.ParseFailure(ToParseError(ex)); }

        var paths = ListPaths(doc);

        XPathNavigator? node;
        try { node = doc.CreateNavigator().SelectSingleNode(keyPath); }
        catch (XPathException ex) { return ConfigFileEditOutcome.ParseFailure(new ConfigFileParseError($"'{keyPath}' is not a valid XPath expression: {ex.Message}", null, null)); }

        if (node is null) return ConfigFileEditOutcome.KeyNotFound(paths);

        node.SetValue(newValue);

        using var writer = new StringWriter();
        doc.Save(writer, SaveOptions.DisableFormatting);
        return ConfigFileEditOutcome.Success(writer.ToString());
    }

    private static XDocument Load(string content) =>
        XDocument.Parse(content, LoadOptions.PreserveWhitespace);

    private static ConfigFileParseError ToParseError(XmlException ex) =>
        new(ex.Message, ex.LineNumber > 0 ? ex.LineNumber : null, ex.LinePosition > 0 ? ex.LinePosition : null);

    /// <summary>
    /// Every attribute path, and every leaf element's (no child elements) text path — the two shapes
    /// a config value actually lives in for this format (<c>&lt;add name="x" value="y"/&gt;</c> and
    /// <c>&lt;Setting&gt;y&lt;/Setting&gt;</c>). Siblings with the same tag are disambiguated by a
    /// <c>name</c>/<c>key</c> attribute when one exists (the idiomatic case — every real
    /// <c>web.config</c> <c>&lt;add&gt;</c> list works this way), falling back to a 1-based position
    /// predicate otherwise.
    /// </summary>
    private static List<string> ListPaths(XDocument doc)
    {
        var paths = new List<string>();
        if (doc.Root is not null) Walk(doc.Root, "/" + doc.Root.Name.LocalName, paths);
        return paths;
    }

    private static void Walk(XElement element, string path, List<string> paths)
    {
        foreach (var attr in element.Attributes())
            if (!attr.IsNamespaceDeclaration)
                paths.Add($"{path}/@{attr.Name.LocalName}");

        var children = element.Elements().ToList();
        if (children.Count == 0)
        {
            var text = element.Nodes().OfType<XText>().Select(t => t.Value).FirstOrDefault();
            if (text is not null) paths.Add(path);
            return;
        }

        foreach (var group in children.GroupBy(c => c.Name.LocalName))
        {
            var siblings = group.ToList();
            foreach (var (child, i) in siblings.Select((c, i) => (c, i)))
            {
                var discriminator = DiscriminatorFor(child, siblings, i);
                Walk(child, $"{path}/{child.Name.LocalName}{discriminator}", paths);
            }
        }
    }

    private static string DiscriminatorFor(XElement element, List<XElement> siblings, int index)
    {
        if (siblings.Count == 1) return string.Empty;

        foreach (var key in new[] { "name", "key" })
        {
            var value = element.Attribute(key)?.Value;
            if (!string.IsNullOrEmpty(value)) return $"[@{key}='{value}']";
        }

        return $"[{index + 1}]";
    }
}
