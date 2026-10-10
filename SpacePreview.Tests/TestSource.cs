using System.Collections.Concurrent;

namespace SpacePreview.Tests;

internal static class TestSource
{
    private static readonly Lazy<string> Root = new(FindRoot);
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string FilePath(string relativePath) =>
        Path.Combine(Root.Value, relativePath);

    /// <summary>
    /// Reads a source file. A class split into partial files reads as one
    /// source: asking for PreviewWindow.xaml.cs returns it followed by every
    /// PreviewWindow.*.cs beside it, so moving a method between the partials
    /// neither breaks a lookup nor silently empties a whole-file assertion.
    /// </summary>
    public static string Read(string relativePath) =>
        Cache.GetOrAdd(FilePath(relativePath), ReadWithPartials);

    private static string ReadWithPartials(string path)
    {
        if (!path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return File.ReadAllText(path);
        }

        var directory = Path.GetDirectoryName(path)!;
        var fileName = Path.GetFileName(path);
        var stem = fileName[..fileName.IndexOf('.')];
        var partials = Directory.EnumerateFiles(directory, stem + ".*.cs")
            .Where(candidate => !string.Equals(candidate, path, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase);
        return string.Join(Environment.NewLine, new[] { path }.Concat(partials).Select(File.ReadAllText));
    }

    public static string Method(string relativePath, string methodName) =>
        MethodFrom(Read(relativePath), methodName);

    public static string MethodFrom(string source, string methodName) =>
        ExtractMethodBody(source, methodName, codeOnly: false);

    public static string CodeMethod(string relativePath, string methodName) =>
        ExtractMethodBody(Read(relativePath), methodName, codeOnly: true);

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SpacePreview.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find SpacePreview.sln from test output directory.");
    }

    private static string ExtractMethodBody(string source, string methodName, bool codeOnly)
    {
        var code = StripStringsAndComments(source);
        var methodIndex = FindMethodDeclaration(code, methodName);
        Assert.True(methodIndex >= 0, $"Could not find method {methodName}.");

        var closeParen = code.IndexOf(')', methodIndex);
        var bodyStart = SkipWhitespace(code, closeParen + 1);
        var resultSource = codeOnly ? code : source;
        if (bodyStart + 1 < code.Length && code[bodyStart] == '=' && code[bodyStart + 1] == '>')
        {
            var expressionStart = bodyStart + 2;
            var expressionEnd = code.IndexOf(';', expressionStart);
            Assert.True(expressionEnd >= 0, $"Could not find expression body for {methodName}.");
            return resultSource[expressionStart..expressionEnd];
        }

        Assert.True(bodyStart < code.Length && code[bodyStart] == '{', $"Could not find method body for {methodName}.");
        var closeBrace = FindMatchingBrace(code, bodyStart);
        Assert.True(closeBrace > bodyStart, $"Could not find matching brace for {methodName}.");

        return resultSource[(bodyStart + 1)..closeBrace];
    }

    private static string StripStringsAndComments(string source)
    {
        var output = new char[source.Length];
        var inString = false;
        var inChar = false;
        var inLineComment = false;
        var inBlockComment = false;
        var inVerbatimString = false;
        var rawQuoteCount = 0;

        for (var i = 0; i < source.Length; i++)
        {
            var current = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (rawQuoteCount > 0)
            {
                output[i] = current is '\r' or '\n' ? current : ' ';
                if (current == '"')
                {
                    var quoteCount = CountQuotes(source, i);
                    if (quoteCount >= rawQuoteCount)
                    {
                        Array.Fill(output, ' ', i, quoteCount);
                        rawQuoteCount = 0;
                        i += quoteCount - 1;
                    }
                }

                continue;
            }

            if (inLineComment)
            {
                output[i] = current is '\r' or '\n' ? current : ' ';
                if (current is '\r' or '\n')
                {
                    inLineComment = false;
                }

                continue;
            }

            if (inBlockComment)
            {
                output[i] = current is '\r' or '\n' ? current : ' ';
                if (current == '*' && next == '/')
                {
                    output[i + 1] = ' ';
                    inBlockComment = false;
                    i++;
                }

                continue;
            }

            if (inString)
            {
                output[i] = current is '\r' or '\n' ? current : ' ';
                if (inVerbatimString)
                {
                    if (current == '"' && next == '"')
                    {
                        output[i + 1] = ' ';
                        i++;
                    }
                    else if (current == '"')
                    {
                        inString = false;
                        inVerbatimString = false;
                    }
                }
                else if (current == '\\')
                {
                    if (i + 1 < source.Length)
                    {
                        output[i + 1] = ' ';
                        i++;
                    }
                }
                else if (current == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (inChar)
            {
                output[i] = current is '\r' or '\n' ? current : ' ';
                if (current == '\\')
                {
                    if (i + 1 < source.Length)
                    {
                        output[i + 1] = ' ';
                        i++;
                    }
                }
                else if (current == '\'')
                {
                    inChar = false;
                }

                continue;
            }

            if (current == '/' && next == '/')
            {
                output[i] = ' ';
                output[i + 1] = ' ';
                inLineComment = true;
                i++;
                continue;
            }

            if (current == '/' && next == '*')
            {
                output[i] = ' ';
                output[i + 1] = ' ';
                inBlockComment = true;
                i++;
                continue;
            }

            if (current == '"')
            {
                var quoteCount = CountQuotes(source, i);
                if (quoteCount >= 3)
                {
                    Array.Fill(output, ' ', i, quoteCount);
                    rawQuoteCount = quoteCount;
                    i += quoteCount - 1;
                    continue;
                }

                output[i] = ' ';
                inString = true;
                inVerbatimString = i > 0 && source[i - 1] == '@'
                    || i > 1 && source[i - 1] == '$' && source[i - 2] == '@';
                continue;
            }

            if (current == '\'')
            {
                output[i] = ' ';
                inChar = true;
                continue;
            }

            output[i] = current;
        }

        return new string(output);
    }

    private static int CountQuotes(string source, int index)
    {
        var count = 0;
        while (index + count < source.Length && source[index + count] == '"')
        {
            count++;
        }

        return count;
    }

    private static int FindMethodDeclaration(string source, string methodName)
    {
        var start = 0;
        while (true)
        {
            var methodIndex = source.IndexOf(methodName + "(", start, StringComparison.Ordinal);
            if (methodIndex < 0)
            {
                return -1;
            }

            var closeParen = source.IndexOf(')', methodIndex);
            if (closeParen < 0)
            {
                return -1;
            }

            var next = SkipWhitespace(source, closeParen + 1);

            var expressionBody = methodName.Any(char.IsWhiteSpace)
                && next + 1 < source.Length
                && source[next] == '='
                && source[next + 1] == '>';
            if (next < source.Length && (source[next] == '{' || expressionBody))
            {
                return methodIndex;
            }

            start = methodIndex + methodName.Length;
        }
    }

    private static int SkipWhitespace(string source, int index)
    {
        while (index < source.Length && char.IsWhiteSpace(source[index]))
        {
            index++;
        }

        return index;
    }

    private static int FindMatchingBrace(string source, int openBrace)
    {
        var depth = 0;
        for (var i = openBrace; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}' && --depth == 0)
            {
                return i;
            }
        }

        return -1;
    }
}
