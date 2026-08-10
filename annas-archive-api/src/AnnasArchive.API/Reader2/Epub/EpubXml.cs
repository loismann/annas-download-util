using System.Xml.Linq;

namespace AnnasArchive.API.Reader2.Epub;

/// <summary>
/// The handful of things every EPUB XML read needs, in one place.
///
/// <para>Everything here ignores namespaces and treats malformed XML as absent
/// rather than fatal. That is deliberate rather than lazy — EPUBs in the wild
/// routinely declare the wrong OPF or Dublin Core namespace, and a strict reader
/// rejects books that every e-reader opens happily.</para>
///
/// <para>Shared so the package reader and the navigation reader cannot drift
/// into disagreeing about what "an element called <c>item</c>" means.</para>
/// </summary>
internal static class EpubXml
{
    /// <summary>Descendants with this local name, whatever namespace they declare.</summary>
    public static IEnumerable<XElement> Named(this XContainer container, string localName) =>
        container.Descendants().Where(e => e.Name.LocalName == localName);

    /// <summary>The first descendant with this local name, or null.</summary>
    public static XElement? FirstNamed(this XContainer container, string localName) =>
        container.Named(localName).FirstOrDefault();

    /// <summary>An attribute's value ignoring its namespace, or "" when absent.</summary>
    public static string Attr(XElement? element, string name) =>
        element?.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value ?? "";

    /// <summary>Parses, treating unparseable XML as a document that is not there.</summary>
    public static XDocument? TryParse(string xml)
    {
        try { return XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { return null; }
    }

    /// <inheritdoc cref="TryParse"/>
    public static XDocument? TryLoad(Stream stream)
    {
        try { return XDocument.Load(stream); }
        catch (System.Xml.XmlException) { return null; }
    }
}
