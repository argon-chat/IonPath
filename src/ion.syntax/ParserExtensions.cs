namespace ion.syntax;

using Pidgin;
using static Pidgin.Parser;
using static Pidgin.Parser<char>;

public static class ParserExtensions
{
    public static Parser<TToken, IEnumerable<T>> ManyBetween<TToken, T>(
        this Parser<TToken, T> parser,
        Parser<TToken, char> open,
        Parser<TToken, char> close)
        => parser.Many().Between(open, close);
}