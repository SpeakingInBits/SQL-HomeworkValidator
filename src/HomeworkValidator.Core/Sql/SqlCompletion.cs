using System.Text.RegularExpressions;

namespace HomeworkValidator.Core.Sql;

/// <summary>What a suggestion is. Suggestions are listed in this order.</summary>
public enum CompletionKind
{
    Column,
    Table,
    Function,
    Keyword,
}

/// <param name="Text">What's inserted: a name (in brackets if it needs them) or an upper-case keyword.</param>
/// <param name="Detail">A short description, e.g. "money · Invoices", "alias for Vendors" or "function".</param>
public sealed record CompletionItem(string Text, CompletionKind Kind, string Detail);

/// <param name="Start">Where the word being typed starts. A chosen suggestion replaces the text from here to the caret.</param>
public sealed record CompletionResult(int Start, IReadOnlyList<CompletionItem> Items)
{
    public static CompletionResult None(int caret) => new(caret, []);
}

/// <summary>
/// Suggests keywords, functions, tables and columns for the word at the caret, roughly the way SSMS does:
/// after FROM or JOIN it lists tables; in SELECT, WHERE, ON and similar clauses it lists the columns of every table in the
/// statement's FROM clause, so a column can be typed without its table name; after "alias." it lists that table's columns.
/// It reads tokens rather than parsing, because the SQL is usually half typed and wouldn't parse.
/// </summary>
public static partial class SqlCompletion
{
    public static CompletionResult Complete(string text, int caret, SqlCatalog catalog)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        var all = Lexer.Tokenize(text);

        // Nothing inside comments, strings or bracketed names, or while typing a number or a variable.
        var start = caret;
        if (all.FirstOrDefault(t => t.Start < caret && caret <= t.End) is { } at)
        {
            switch (at.Kind)
            {
                case TokenKind.Comment or TokenKind.String when caret < at.End || !at.Closed:
                case TokenKind.Number:
                case TokenKind.Word when at.Quoted && (caret < at.End || !at.Closed):
                case TokenKind.Word when at.Value[0] == '@':
                    return CompletionResult.None(caret);
                case TokenKind.Word when !at.Quoted:
                    start = at.Start;
                    break;
            }
        }

        var tokens = all.Where(t => t.Kind != TokenKind.Comment).ToList();
        var word = tokens.FindIndex(t => t.Start >= start);
        if (word < 0)
            word = tokens.Count;

        var starts = StatementStarts(tokens);
        var from = starts.Last(s => s <= word);
        var to = starts.FirstOrDefault(s => s > from, tokens.Count);
        var refs = TableReferences(tokens, from, to, catalog);

        var previous = word > from ? tokens[word - 1] : null;
        if (previous is not null && previous.Is("."))
            return new CompletionResult(start, Qualified(tokens, word - 1, from, refs, catalog).ToList());
        if (previous is not null && previous.IsWord("AS"))
            return CompletionResult.None(caret); // naming an alias

        var items = FindClause(tokens, from, word, catalog) switch
        {
            Clause.TableName => TableItems(catalog).Concat(KeywordItems()),
            Clause.Expression => ExpressionItems(refs).Concat(FunctionItems()).Concat(KeywordItems()),
            Clause.InsertColumns { Table: var table } => ColumnItems(table),
            _ => FunctionItems().Concat(KeywordItems()),
        };
        return new CompletionResult(start, items.ToList());
    }

    // ----- Where the caret is -----

    private abstract record Clause
    {
        /// <summary>After FROM, JOIN, UPDATE, INTO or TABLE, where a table name goes.</summary>
        public sealed record TableName : Clause;

        /// <summary>In SELECT, WHERE, ON, GROUP BY, ORDER BY, HAVING or SET, where columns go.</summary>
        public sealed record Expression : Clause;

        /// <summary>In the column list of INSERT INTO table (...).</summary>
        public sealed record InsertColumns(CatalogTable Table) : Clause;

        /// <summary>Anywhere else, such as the start of a statement or after a table name.</summary>
        public sealed record Other : Clause;
    }

    private static readonly HashSet<string> TableClauses = new(StringComparer.OrdinalIgnoreCase) { "FROM", "JOIN", "UPDATE", "INTO", "TABLE", "DELETE" };
    private static readonly HashSet<string> ExpressionClauses = new(StringComparer.OrdinalIgnoreCase) { "SELECT", "WHERE", "ON", "HAVING", "BY", "SET" };
    private static readonly HashSet<string> OtherClauses = new(StringComparer.OrdinalIgnoreCase)
        { "VALUES", "INSERT", "CREATE", "ALTER", "DROP", "DECLARE", "EXEC", "EXECUTE", "PRINT", "USE", "TRUNCATE" };

    /// <summary>Walks back from the caret to the keyword that starts its clause, skipping anything in parentheses that has closed.</summary>
    private static Clause FindClause(List<Token> tokens, int from, int word, SqlCatalog catalog)
    {
        var depth = 0;
        for (var i = word - 1; i >= from; i--)
        {
            var token = tokens[i];
            if (token.Is(")"))
            {
                depth++;
                continue;
            }

            if (token.Is("("))
            {
                if (depth > 0)
                {
                    depth--;
                    continue;
                }

                // The caret is inside these parentheses. A function call or subquery goes on to the clause around it.
                if (i - 2 >= from && tokens[i - 2].IsWord("INTO") && catalog.Find(tokens[i - 1].Value) is { } target)
                    return new Clause.InsertColumns(target);
                if (i - 2 >= from && tokens[i - 2].IsWord("TABLE"))
                    return new Clause.Other(); // CREATE TABLE's column definitions
                continue;
            }

            if (depth > 0 || token.Kind != TokenKind.Word || token.Quoted)
                continue;

            if (TableClauses.Contains(token.Value))
            {
                // Right after the keyword, or after a comma in FROM a, b. Further on, the table is named and a keyword comes next.
                var previous = tokens[word - 1];
                return previous == token || (previous.Is(",") && token.IsWord("FROM")) ? new Clause.TableName() : new Clause.Other();
            }

            if (ExpressionClauses.Contains(token.Value))
                return new Clause.Expression();
            if (OtherClauses.Contains(token.Value))
                return new Clause.Other();
        }

        return new Clause.Other();
    }

    // ----- Statements and their tables -----

    private static readonly HashSet<string> StatementKeywords = new(StringComparer.OrdinalIgnoreCase)
        { "SELECT", "INSERT", "UPDATE", "DELETE", "MERGE", "CREATE", "ALTER", "DROP", "TRUNCATE", "DECLARE", "EXEC", "EXECUTE", "PRINT", "IF", "WHILE", "BEGIN", "USE" };

    /// <summary>Words after which a statement keyword continues the statement: SELECT after UNION, DELETE in ON DELETE CASCADE, IF in DROP TABLE IF EXISTS.</summary>
    private static readonly HashSet<string> Continuing = new(StringComparer.OrdinalIgnoreCase)
        { "UNION", "ALL", "EXCEPT", "INTERSECT", "ON", "THEN", "AS", "OR", "TABLE", "VIEW", "PROCEDURE", "PROC", "FUNCTION", "DATABASE", "INDEX", "SCHEMA", "TRIGGER" };

    /// <summary>
    /// Where each statement starts, as token indexes. Students often leave out semicolons, so a statement keyword such as
    /// SELECT also starts one, unless it obviously continues the one before (UNION SELECT, INSERT INTO t SELECT, a CTE's query).
    /// </summary>
    private static List<int> StatementStarts(List<Token> tokens)
    {
        var starts = new List<int> { 0 };
        var depth = 0;
        string? first = null;
        var insertNeedsSource = false;
        var cteQuerySeen = false;

        void Start(int index, string? word)
        {
            starts.Add(index);
            depth = 0;
            first = word;
            insertNeedsSource = word is not null && word.Equals("INSERT", StringComparison.OrdinalIgnoreCase);
            cteQuerySeen = false;
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Is("("))
                depth++;
            else if (token.Is(")"))
                depth = Math.Max(0, depth - 1);
            else if (token.Is(";") || token.IsWord("GO"))
                Start(i + 1, null);
            else if (depth == 0 && token.Kind == TokenKind.Word && !token.Quoted)
            {
                var word = token.Value.ToUpperInvariant();
                if (first is null)
                {
                    first = word;
                    insertNeedsSource = word == "INSERT";
                    continue;
                }

                if (insertNeedsSource && word is "SELECT" or "VALUES" or "EXEC" or "EXECUTE")
                {
                    insertNeedsSource = false;
                    continue;
                }

                if (!StatementKeywords.Contains(word))
                    continue;

                var previous = tokens[i - 1];
                var next = i + 1 < tokens.Count ? tokens[i + 1] : null;
                if (previous.Kind == TokenKind.Word && !previous.Quoted && Continuing.Contains(previous.Value))
                    continue;
                if (first == "WITH" && previous.Is(")") && !cteQuerySeen)
                {
                    cteQuerySeen = true;
                    continue;
                }
                if (first == "ALTER" && word is "ALTER" or "DROP" && (next?.IsWord("COLUMN") == true || next?.IsWord("CONSTRAINT") == true))
                    continue;

                Start(i, word);
            }
        }

        return starts;
    }

    private sealed record TableReference(CatalogTable Table, string? Alias)
    {
        public string Name => Alias ?? Table.Name;
    }

    /// <summary>The known tables named after FROM, JOIN, UPDATE, INTO or DELETE anywhere in the statement, with their aliases.</summary>
    private static List<TableReference> TableReferences(List<Token> tokens, int from, int to, SqlCatalog catalog)
    {
        var refs = new List<TableReference>();
        for (var i = from; i < to; i++)
        {
            if (!(tokens[i].IsWord("FROM") || tokens[i].IsWord("JOIN") || tokens[i].IsWord("UPDATE") || tokens[i].IsWord("INTO") || tokens[i].IsWord("DELETE")))
                continue;

            var j = ReadTableReference(tokens, i + 1, to, catalog, refs);
            while (tokens[i].IsWord("FROM") && j + 1 < to && tokens[j].Is(","))
                j = ReadTableReference(tokens, j + 1, to, catalog, refs);
        }

        return refs;
    }

    /// <summary>Reads [schema.]table [[AS] alias] at <paramref name="i"/> and returns the index after it.</summary>
    private static int ReadTableReference(List<Token> tokens, int i, int to, SqlCatalog catalog, List<TableReference> refs)
    {
        if (i >= to)
            return i;

        string? name = null;
        if (tokens[i].Is("("))
        {
            // A derived table, (SELECT ...) AS d. Its columns aren't known, so only skip past it.
            for (var depth = 0; i < to; i++)
            {
                if (tokens[i].Is("("))
                    depth++;
                else if (tokens[i].Is(")") && --depth == 0)
                    break;
            }

            i++;
        }
        else if (IsName(tokens[i]))
        {
            name = tokens[i++].Value;
            while (i + 1 < to && tokens[i].Is(".") && IsName(tokens[i + 1]))
            {
                name = tokens[i + 1].Value;
                i += 2;
            }
        }
        else
        {
            return i;
        }

        string? alias = null;
        if (i < to && tokens[i].IsWord("AS"))
            i++;
        if (i < to && IsName(tokens[i]))
            alias = tokens[i++].Value;

        if (name is not null && catalog.Find(name) is { } table)
            refs.Add(new TableReference(table, alias));
        return i;
    }

    private static bool IsName(Token token) =>
        token.Kind == TokenKind.Word && (token.Quoted || (!Keywords.Contains(token.Value) && token.Value[0] != '@'));

    // ----- Suggestions -----

    /// <summary>After "name.": the columns of the table or alias, or the tables after dbo.</summary>
    private static IEnumerable<CompletionItem> Qualified(List<Token> tokens, int dot, int from, List<TableReference> refs, SqlCatalog catalog)
    {
        if (dot - 1 < from || tokens[dot - 1].Kind != TokenKind.Word)
            return [];

        var qualifier = tokens[dot - 1].Value;
        var table = refs.FirstOrDefault(r => r.Alias?.Equals(qualifier, StringComparison.OrdinalIgnoreCase) == true)?.Table
                    ?? catalog.Find(qualifier);
        if (table is not null)
            return ColumnItems(table);
        return qualifier.Equals("dbo", StringComparison.OrdinalIgnoreCase) ? TableItems(catalog) : [];
    }

    private static IEnumerable<CompletionItem> ColumnItems(CatalogTable table) =>
        table.Columns.Select(c => new CompletionItem(Quote(c.Name), CompletionKind.Column, $"{c.Type} · {table.Name}"));

    private static IEnumerable<CompletionItem> TableItems(SqlCatalog catalog) =>
        catalog.Tables.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => new CompletionItem(Quote(t.Name), CompletionKind.Table, $"table · {t.Columns.Count} columns"));

    /// <summary>The columns of every table in the statement, then the tables' names and aliases for writing alias.column.</summary>
    private static IEnumerable<CompletionItem> ExpressionItems(List<TableReference> refs)
    {
        var tables = refs.Select(r => r.Table).Distinct().ToList();
        var columns = tables
            .SelectMany(t => t.Columns.Select(c => (Table: t, Column: c)))
            .GroupBy(x => x.Column.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CompletionItem(Quote(g.Key), CompletionKind.Column, g.Count() == 1
                ? $"{g.First().Column.Type} · {g.First().Table.Name}"
                : $"in {string.Join(", ", g.Select(x => x.Table.Name))}"));

        var names = refs
            .DistinctBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(r => new CompletionItem(Quote(r.Name), CompletionKind.Table, r.Alias is null ? "table" : $"alias for {r.Table.Name}"));

        return columns.Concat(names);
    }

    private static IEnumerable<CompletionItem> FunctionItems() =>
        Functions.Order(StringComparer.Ordinal).Select(f => new CompletionItem(f, CompletionKind.Function, "function"));

    private static IEnumerable<CompletionItem> KeywordItems() =>
        Keywords.Order(StringComparer.Ordinal).Select(k => new CompletionItem(k, CompletionKind.Keyword, DataTypes.Contains(k) ? "data type" : "keyword"));

    /// <summary>The name as it must be written: in brackets if it has spaces or other odd characters, or is a keyword.</summary>
    private static string Quote(string name) =>
        PlainName().IsMatch(name) && !Keywords.Contains(name) ? name : $"[{name.Replace("]", "]]")}]";

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex PlainName();

    private static readonly HashSet<string> DataTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "BIGINT", "BIT", "CHAR", "DATE", "DATETIME", "DATETIME2", "DATETIMEOFFSET", "DECIMAL", "FLOAT", "INT", "MONEY", "NCHAR",
        "NUMERIC", "NVARCHAR", "REAL", "SMALLDATETIME", "SMALLINT", "SMALLMONEY", "TIME", "TINYINT", "UNIQUEIDENTIFIER",
        "VARBINARY", "VARCHAR",
    };

    /// <summary>Keywords students use in this course, and the data types. These are never taken for table aliases.</summary>
    private static readonly HashSet<string> Keywords = new(
    [
        "ADD", "ALL", "ALTER", "AND", "ANY", "APPLY", "AS", "ASC", "BEGIN", "BETWEEN", "BY", "CASCADE", "CASE", "CATCH", "CHECK",
        "CLUSTERED", "COLUMN", "COMMIT", "CONSTRAINT", "CREATE", "CROSS", "DATABASE", "DECLARE", "DEFAULT", "DELETE", "DESC",
        "DISTINCT", "DROP", "ELSE", "END", "EXCEPT", "EXEC", "EXISTS", "FETCH", "FOREIGN", "FROM", "FULL", "FUNCTION", "GO",
        "GROUP", "HAVING", "IDENTITY", "IF", "IN", "INDEX", "INNER", "INSERT", "INTERSECT", "INTO", "IS", "JOIN", "KEY", "LEFT",
        "LIKE", "MERGE", "NEXT", "NONCLUSTERED", "NOT", "NULL", "OFFSET", "ON", "ONLY", "OR", "ORDER", "OUTER", "OUTPUT", "OVER",
        "PARTITION", "PERCENT", "PRIMARY", "PRINT", "PROCEDURE", "REFERENCES", "RETURN", "RETURNS", "RIGHT", "ROLLBACK", "ROWS",
        "SELECT", "SET", "TABLE", "THEN", "TIES", "TOP", "TRANSACTION", "TRUNCATE", "TRY", "UNION", "UNIQUE", "UPDATE", "USE",
        "USING", "VALUES", "VIEW", "WHEN", "WHERE", "WHILE", "WITH",
        .. DataTypes,
    ], StringComparer.OrdinalIgnoreCase);

    private static readonly string[] Functions =
    [
        "ABS", "AVG", "CAST", "CEILING", "CHARINDEX", "CHOOSE", "COALESCE", "CONCAT", "CONVERT", "COUNT", "DATEADD", "DATEDIFF",
        "DATENAME", "DATEPART", "DAY", "DENSE_RANK", "EOMONTH", "FLOOR", "FORMAT", "GETDATE", "IIF", "ISNULL", "LEN", "LOWER",
        "LTRIM", "MAX", "MIN", "MONTH", "NTILE", "NULLIF", "PATINDEX", "POWER", "RANK", "REPLACE", "REPLICATE", "REVERSE", "ROUND",
        "ROW_NUMBER", "RTRIM", "SQRT", "STR", "STRING_AGG", "SUBSTRING", "SUM", "SYSDATETIME", "TRIM", "TRY_CAST", "TRY_CONVERT",
        "UPPER", "YEAR",
    ];

    // ----- Tokens -----

    private enum TokenKind
    {
        Word,
        Number,
        String,
        Comment,
        Symbol,
    }

    /// <param name="Value">A word without its brackets or quotes, or the symbol.</param>
    /// <param name="Quoted">A [bracketed] or "quoted" name.</param>
    /// <param name="Closed">False for a string, comment or quoted name that runs to the end of the text, and for -- comments.</param>
    private sealed record Token(TokenKind Kind, int Start, int End, string Value, bool Quoted = false, bool Closed = true)
    {
        public bool Is(string symbol) => Kind == TokenKind.Symbol && Value == symbol;
        public bool IsWord(string word) => Kind == TokenKind.Word && !Quoted && Value.Equals(word, StringComparison.OrdinalIgnoreCase);
    }

    private static class Lexer
    {
        public static List<Token> Tokenize(string s)
        {
            var tokens = new List<Token>();
            var i = 0;
            while (i < s.Length)
            {
                var c = s[i];
                var start = i;
                if (char.IsWhiteSpace(c))
                {
                    i++;
                }
                else if (c == '-' && At(s, i + 1) == '-')
                {
                    while (i < s.Length && s[i] != '\n')
                        i++;
                    tokens.Add(new Token(TokenKind.Comment, start, i, "", Closed: false));
                }
                else if (c == '/' && At(s, i + 1) == '*')
                {
                    var close = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                    i = close < 0 ? s.Length : close + 2;
                    tokens.Add(new Token(TokenKind.Comment, start, i, "", Closed: close >= 0));
                }
                else if (c == '\'' || (c is 'N' or 'n' && At(s, i + 1) == '\''))
                {
                    if (c != '\'')
                        i++;
                    (i, var closed) = SkipQuoted(s, i, '\'');
                    tokens.Add(new Token(TokenKind.String, start, i, "", Closed: closed));
                }
                else if (c is '[' or '"')
                {
                    (i, var closed) = SkipQuoted(s, i, c == '[' ? ']' : '"');
                    var value = s[(start + 1)..(closed ? i - 1 : i)].Replace(c == '[' ? "]]" : "\"\"", c == '[' ? "]" : "\"");
                    tokens.Add(new Token(TokenKind.Word, start, i, value, Quoted: true, Closed: closed));
                }
                else if (char.IsLetter(c) || c is '_' or '@' or '#')
                {
                    while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '@' or '#' or '$'))
                        i++;
                    tokens.Add(new Token(TokenKind.Word, start, i, s[start..i]));
                }
                else if (char.IsDigit(c))
                {
                    while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '.'))
                        i++;
                    tokens.Add(new Token(TokenKind.Number, start, i, s[start..i]));
                }
                else
                {
                    i++;
                    tokens.Add(new Token(TokenKind.Symbol, start, i, c.ToString()));
                }
            }

            return tokens;
        }

        private static char At(string s, int i) => i < s.Length ? s[i] : '\0';

        /// <summary>Skips from an opening quote or bracket past its close; a doubled close is an escaped one.</summary>
        private static (int End, bool Closed) SkipQuoted(string s, int open, char close)
        {
            for (var i = open + 1; i < s.Length; i++)
            {
                if (s[i] != close)
                    continue;
                if (At(s, i + 1) == close)
                {
                    i++;
                    continue;
                }

                return (i + 1, true);
            }

            return (s.Length, false);
        }
    }
}
