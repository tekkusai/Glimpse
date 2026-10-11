using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace SpacePreview.Utilities;

/// <summary>
/// XDocument walks up to the root for every element it adds, and a value
/// read from an element recurses once per level, so a file nested far deeper
/// than any real document stalls the load for minutes or overflows the stack.
/// A streaming pass refuses it before the tree is built.
/// </summary>
internal static class XmlNesting
{
    public const int MaxDepth = 256;

    /// <summary>The document <paramref name="open"/> reads, opened twice: once to measure, once to load.</summary>
    public static XDocument Load(Func<Stream> open, XmlReaderSettings settings, LoadOptions options)
    {
        using (var stream = open())
        using (var reader = XmlReader.Create(stream, settings))
        {
            while (reader.Read())
            {
                if (reader.Depth > MaxDepth)
                {
                    throw new InvalidDataException("The XML nests too deeply to preview.");
                }
            }
        }

        using var again = open();
        using var loader = XmlReader.Create(again, settings);
        return XDocument.Load(loader, options);
    }
}
