using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace WorkshopSentinel.Services;

/// <summary>
/// Node in a Valve KeyValues / ACF tree. Either a scalar string or an object (dict of children).
/// Reads are tolerant — missing keys return null rather than throwing, so callers can do
/// `root["WorkshopItemsInstalled"]?["1369573612"]?["timeupdated"]?.AsLong()` without try/catch chains.
/// </summary>
public sealed class AcfNode
{
    private readonly string? _scalar;
    private readonly Dictionary<string, AcfNode>? _children;

    private AcfNode(string scalar) { _scalar = scalar; }
    private AcfNode(Dictionary<string, AcfNode> children) { _children = children; }

    public bool IsObject => _children is not null;
    public bool IsScalar => _scalar is not null;

    public IReadOnlyDictionary<string, AcfNode> Children =>
        _children ?? (IReadOnlyDictionary<string, AcfNode>)EmptyDict;

    private static readonly Dictionary<string, AcfNode> EmptyDict = new();

    /// <summary>Indexer returns null for unknown keys (tolerant); throws on scalars (programmer error).</summary>
    public AcfNode? this[string key]
    {
        get
        {
            if (_children is null) throw new InvalidOperationException(
                "Indexer called on a scalar AcfNode. Use AsString()/AsLong() instead.");
            return _children.TryGetValue(key, out var v) ? v : null;
        }
    }

    public string AsString() => _scalar ?? throw new InvalidOperationException(
        "AsString() called on an object AcfNode. Use Children or the indexer.");

    public long AsLong() => long.TryParse(AsString(), out var v) ? v : 0;
    public ulong AsULong() => ulong.TryParse(AsString(), out var v) ? v : 0;

    public static AcfNode Scalar(string value) => new(value);
    public static AcfNode Object(Dictionary<string, AcfNode> children) => new(children);

    /// <summary>
    /// Parse a full ACF document. The Valve convention is a single top-level wrapper key
    /// (e.g. "AppWorkshop" or "AppState") whose body is the root object. We unwrap that and
    /// return the body, so callers immediately see the useful keys.
    /// </summary>
    public static AcfNode Parse(string text)
    {
        var p = new Parser(text);
        p.SkipWhitespace();
        if (p.Eof) throw new FormatException("ACF input is empty.");

        // Read the wrapper key (e.g. "AppWorkshop"), then the opening brace.
        _ = p.ReadToken();
        p.SkipWhitespace();
        p.Expect('{');
        return p.ParseObject();
    }

    // Recursive-descent parser, tolerant: missing closing braces stop gracefully at EOF.
    private sealed class Parser
    {
        private readonly string _text;
        private int _pos;
        public Parser(string text) { _text = text; }

        public bool Eof => _pos >= _text.Length;

        public AcfNode ParseObject()
        {
            // Caller has already consumed the opening '{'.
            var dict = new Dictionary<string, AcfNode>(StringComparer.Ordinal);
            while (true)
            {
                SkipWhitespace();
                if (Eof) return AcfNode.Object(dict);            // tolerate truncation
                if (_text[_pos] == '}') { _pos++; return AcfNode.Object(dict); }

                var key = ReadToken();
                SkipWhitespace();
                if (Eof) { dict[key] = AcfNode.Scalar(""); return AcfNode.Object(dict); }

                if (_text[_pos] == '{')
                {
                    _pos++;
                    // Last-wins on duplicate keys — Valve's own usage seems to assume uniqueness;
                    // we don't merge, we overwrite.
                    dict[key] = ParseObject();
                }
                else
                {
                    dict[key] = AcfNode.Scalar(ReadToken());
                }
            }
        }

        public string ReadToken()
        {
            SkipWhitespace();
            if (Eof) return string.Empty;
            return _text[_pos] == '"' ? ReadQuoted() : ReadBareword();
        }

        private string ReadQuoted()
        {
            _pos++; // consume opening quote
            var sb = new StringBuilder();
            while (!Eof)
            {
                var ch = _text[_pos++];
                if (ch == '"') return sb.ToString();
                if (ch == '\\' && !Eof)
                {
                    var esc = _text[_pos++];
                    sb.Append(esc switch
                    {
                        'n'  => '\n',
                        't'  => '\t',
                        'r'  => '\r',
                        '"'  => '"',
                        '\\' => '\\',
                        _    => esc,        // unknown escapes pass through literally
                    });
                }
                else
                {
                    sb.Append(ch);
                }
            }
            return sb.ToString(); // unterminated — tolerate, return what we have
        }

        private string ReadBareword()
        {
            var sb = new StringBuilder();
            while (!Eof)
            {
                var ch = _text[_pos];
                if (char.IsWhiteSpace(ch) || ch == '{' || ch == '}' || ch == '"') break;
                sb.Append(ch);
                _pos++;
            }
            return sb.ToString();
        }

        public void SkipWhitespace()
        {
            while (!Eof)
            {
                var ch = _text[_pos];
                if (char.IsWhiteSpace(ch)) { _pos++; }
                else if (ch == '/' && _pos + 1 < _text.Length && _text[_pos + 1] == '/')
                {
                    // // line comment to end-of-line
                    while (!Eof && _text[_pos] != '\n') _pos++;
                }
                else
                {
                    break;
                }
            }
        }

        public void Expect(char c)
        {
            if (Eof || _text[_pos] != c)
                throw new FormatException($"Expected '{c}' at position {_pos}.");
            _pos++;
        }
    }

    // ---------- Convenience: read a file from disk ----------

    public static AcfNode ParseFile(string path) => Parse(File.ReadAllText(path));
}
