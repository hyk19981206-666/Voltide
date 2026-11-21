using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Voltide.Core
{
    /// <summary>
    /// Minimal, strict JSON reader for the optional device-name file.
    ///
    /// The project ships as a single exe compiled with csc against the .NET
    /// Framework assemblies only, and it must stay loadable in any host (the
    /// verification loads it into PowerShell too). System.Web.Extensions'
    /// JavaScriptSerializer looks like a framework type but pulls in System.Web,
    /// which is not resolvable outside a web host — measured here as
    /// TypeLoadException "System.Web.Util.Utf16StringValidator". A ~150-line
    /// reader removes that variable; it is deliberately strict (no comments, no
    /// trailing commas, no trailing content) so a broken config fails closed.
    ///
    /// Values map to Dictionary&lt;string,object&gt;, List&lt;object&gt;, string,
    /// double, bool and null.
    /// </summary>
    internal static class JsonReader
    {
        internal static object Parse(string text)
        {
            if (text == null) throw new FormatException("empty document");
            Reader reader = new Reader(text);
            object value = reader.ReadValue();
            reader.SkipWhitespace();
            if (!reader.AtEnd) throw reader.Error("trailing content");
            return value;
        }

        private sealed class Reader
        {
            private readonly string _text;
            private int _index;

            internal Reader(string text)
            {
                _text = text;
                _index = 0;
            }

            internal bool AtEnd { get { return _index >= _text.Length; } }

            internal FormatException Error(string message)
            {
                return new FormatException(message + " at offset " + _index);
            }

            internal void SkipWhitespace()
            {
                while (_index < _text.Length)
                {
                    char c = _text[_index];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n') _index++;
                    else break;
                }
            }

            internal object ReadValue()
            {
                SkipWhitespace();
                if (AtEnd) throw Error("unexpected end of document");

                char c = _text[_index];
                switch (c)
                {
                    case '{': return ReadObject();
                    case '[': return ReadArray();
                    case '"': return ReadString();
                    case 't': Expect("true"); return true;
                    case 'f': Expect("false"); return false;
                    case 'n': Expect("null"); return null;
                    default:
                        if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber();
                        throw Error("unexpected character '" + c + "'");
                }
            }

            private Dictionary<string, object> ReadObject()
            {
                Dictionary<string, object> map = new Dictionary<string, object>(StringComparer.Ordinal);
                _index++;                       // {
                SkipWhitespace();
                if (!AtEnd && _text[_index] == '}') { _index++; return map; }

                while (true)
                {
                    SkipWhitespace();
                    if (AtEnd || _text[_index] != '"') throw Error("expected a property name");
                    string key = ReadString();
                    SkipWhitespace();
                    if (AtEnd || _text[_index] != ':') throw Error("expected ':'");
                    _index++;
                    map[key] = ReadValue();
                    SkipWhitespace();
                    if (AtEnd) throw Error("unterminated object");
                    char c = _text[_index];
                    if (c == ',') { _index++; continue; }
                    if (c == '}') { _index++; return map; }
                    throw Error("expected ',' or '}'");
                }
            }

            private List<object> ReadArray()
            {
                List<object> list = new List<object>();
                _index++;                       // [
                SkipWhitespace();
                if (!AtEnd && _text[_index] == ']') { _index++; return list; }

                while (true)
                {
                    list.Add(ReadValue());
                    SkipWhitespace();
                    if (AtEnd) throw Error("unterminated array");
                    char c = _text[_index];
                    if (c == ',') { _index++; continue; }
                    if (c == ']') { _index++; return list; }
                    throw Error("expected ',' or ']'");
                }
            }

            private string ReadString()
            {
                _index++;                       // opening quote
                StringBuilder sb = new StringBuilder();
                while (true)
                {
                    if (AtEnd) throw Error("unterminated string");
                    char c = _text[_index++];
                    if (c == '"') return sb.ToString();
                    if (c != '\\') { sb.Append(c); continue; }

                    if (AtEnd) throw Error("unterminated escape");
                    char e = _text[_index++];
                    switch (e)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        case '/': sb.Append('/'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u':
                            if (_index + 4 > _text.Length) throw Error("truncated \\u escape");
                            int code;
                            if (!int.TryParse(_text.Substring(_index, 4), NumberStyles.HexNumber,
                                              CultureInfo.InvariantCulture, out code))
                                throw Error("invalid \\u escape");
                            sb.Append((char)code);
                            _index += 4;
                            break;
                        default:
                            throw Error("invalid escape '\\" + e + "'");
                    }
                }
            }

            private double ReadNumber()
            {
                int start = _index;
                if (_text[_index] == '-') _index++;
                while (_index < _text.Length)
                {
                    char c = _text[_index];
                    if ((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') _index++;
                    else break;
                }

                string token = _text.Substring(start, _index - start);
                double value;
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                    throw Error("invalid number '" + token + "'");
                return value;
            }

            private void Expect(string literal)
            {
                if (_index + literal.Length > _text.Length ||
                    string.CompareOrdinal(_text, _index, literal, 0, literal.Length) != 0)
                    throw Error("expected '" + literal + "'");
                _index += literal.Length;
            }
        }
    }
}
