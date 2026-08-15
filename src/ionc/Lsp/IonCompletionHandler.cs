namespace ion.compiler.Lsp;

using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ion.runtime;

public class IonCompletionHandler(IonWorkspace workspace) : CompletionHandlerBase
{
    protected override CompletionRegistrationOptions CreateRegistrationOptions(
        CompletionCapability capability, ClientCapabilities clientCapabilities)
    {
        return new CompletionRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("ion"),
            TriggerCharacters = new Container<string>(":", "<", "#", "{", ",", "\"", "@", "("),
            ResolveProvider = false
        };
    }

    public override Task<CompletionList> Handle(CompletionParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.GetFileSystemPath();
        var content = workspace.GetDocumentContent(uri)
            ?? (File.Exists(uri) ? File.ReadAllText(uri) : null);

        // Attribute positions are answered exclusively: after `@` only an attribute name is legal,
        // inside `@Foo(…)` only that attribute's parameter names, after `on` only a target keyword.
        // Falling through to the general symbol list there would bury the handful of right answers
        // under every type, field and method in the workspace.
        if (content is not null &&
            AttributeCompletions(content, uri, request.Position.Line, request.Position.Character) is { } attribute)
            return Task.FromResult(new CompletionList(attribute));

        // A `with` clause is answered exclusively for the same reason: the only legal word there
        // is the name of a declared mixin, and burying those few under every type, field and
        // method in the workspace is what makes a completion list useless.
        if (content is not null &&
            MixinCompletions(content, request.Position.Line, request.Position.Character) is { } mixins)
            return Task.FromResult(new CompletionList(mixins));

        var items = IonLspHelpers.GetCompletionItems(workspace);

        // Check if we're inside a directive context (#)
        if (content is not null)
        {
            var lines = content.Split('\n');
            var line = request.Position.Line;
            if (line >= 0 && line < lines.Length)
            {
                var lineText = lines[line].TrimEnd('\r');
                var prefix = lineText[..Math.Min(request.Position.Character, lineText.Length)].TrimStart();

                if (prefix.StartsWith("#import") || lineText.TrimStart().StartsWith("#import"))
                {
                    var trimmedLine = lineText.TrimStart();
                    var cursorInLine = request.Position.Character;

                    // Find where { and } are relative to cursor
                    var braceOpen = trimmedLine.IndexOf('{');
                    var braceClose = trimmedLine.IndexOf('}');
                    var fromIdx = trimmedLine.IndexOf("from", StringComparison.Ordinal);

                    if (fromIdx >= 0 && cursorInLine > lineText.IndexOf("from", StringComparison.Ordinal))
                    {
                        // After "from" — suggest module names (including inside quotes)
                        items = GetModuleNameCompletions(uri);
                    }
                    else if (braceOpen >= 0 && (braceClose < 0 || cursorInLine <= lineText.IndexOf('}'))
                             && cursorInLine > lineText.IndexOf('{'))
                    {
                        // Inside { } — suggest type names
                        var moduleName = ExtractModuleNameFromLine(lineText);
                        if (moduleName is not null)
                            items = GetModuleTypeCompletions(uri, moduleName);
                        else
                            items = GetAllExternalTypeCompletions(uri);
                    }
                    else
                    {
                        // Just typed #import, offer snippet
                        items =
                        [
                            new CompletionItem
                            {
                                Label = "#import",
                                Kind = CompletionItemKind.Keyword,
                                InsertText = "#import { $1 } from \"$2\"",
                                InsertTextFormat = InsertTextFormat.Snippet,
                                Detail = "Import types from module"
                            }
                        ];
                    }
                }
                else if (prefix.StartsWith("#use"))
                {
                    // Don't suggest types after #use
                    items = [new CompletionItem
                    {
                        Label = "#use",
                        Kind = CompletionItemKind.Snippet,
                        InsertText = "#use \"$1\"",
                        InsertTextFormat = InsertTextFormat.Snippet,
                        Detail = "Import module (deprecated, use #import)"
                    }];
                }
                else if (prefix.StartsWith("#feature"))
                {
                    items =
                    [
                        new CompletionItem { Label = "std", Kind = CompletionItemKind.Value, Detail = "Standard library" },
                        new CompletionItem { Label = "orleans", Kind = CompletionItemKind.Value, Detail = "Orleans integration" },
                    ];
                }
                else if (prefix.StartsWith("#"))
                {
                    items =
                    [
                        new CompletionItem
                        {
                            Label = "#import",
                            Kind = CompletionItemKind.Keyword,
                            InsertText = "#import { $1 } from \"$2\"",
                            InsertTextFormat = InsertTextFormat.Snippet,
                            Detail = "Import types from module"
                        },
                        new CompletionItem
                        {
                            Label = "#use",
                            Kind = CompletionItemKind.Keyword,
                            InsertText = "#use \"$1\"",
                            InsertTextFormat = InsertTextFormat.Snippet,
                            Detail = "Import module (deprecated)"
                        },
                        new CompletionItem
                        {
                            Label = "#feature",
                            Kind = CompletionItemKind.Keyword,
                            InsertText = "#feature \"$1\"",
                            InsertTextFormat = InsertTextFormat.Snippet,
                            Detail = "Enable feature"
                        },
                    ];
                }
            }
        }

        return Task.FromResult(new CompletionList(items));
    }

    // ═══════════════════════════════════════════════════════════════════
    // Mixins
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The declared mixins, for a cursor inside a <c>with</c> clause — or <see langword="null"/>
    /// when the cursor is not in one, so the caller falls through to the general list.
    /// </summary>
    /// <remarks>
    /// Two names are filtered out because writing them is always a diagnostic: one already listed
    /// in this clause (ION0063) and the declaration's own name, which would be the shortest
    /// possible ION0064 cycle. Everything else is offered, including a mixin whose inclusion here
    /// would collide on a field — that is ION0065 and it depends on the whole expansion, which is
    /// more than a completion list should be deciding.
    /// </remarks>
    private List<CompletionItem>? MixinCompletions(string content, int line, int character)
    {
        var scan = IonCommentScanner.Scan(content);

        if (IonMixinLsp.InWithClause(scan, line, character) is not { } clause)
            return null;

        var declarations = IonMixinLsp.Declarations(workspace);
        var items = new List<CompletionItem>();

        foreach (var (name, mixin) in declarations)
        {
            if (clause.Written.Contains(name, StringComparer.Ordinal))
                continue;

            if (string.Equals(name, clause.Declared, StringComparison.Ordinal))
                continue;

            var contributed = IonMixinLsp.Expand(mixin, declarations);
            var names = string.Join(", ", contributed.Select(f => f.Field.Name.Identifier));

            var documentation = new List<string> { $"```ion\n{IonMixinLsp.Signature(mixin)}\n```" };

            if (!string.IsNullOrWhiteSpace(mixin.Comments))
                documentation.Add(mixin.Comments!);

            documentation.Add(contributed.Count == 0
                ? "Contributes no fields."
                : $"Contributes **{contributed.Count}** field(s), in this order: `{names}`.");

            items.Add(IonLspHelpers.WithDoc(new CompletionItem
            {
                Label = name,
                Kind = CompletionItemKind.Interface,
                Detail = contributed.Count == mixin.Fields.Count
                    ? $"mixin — {contributed.Count} field(s)"
                    // A mixin that composes others brings in more than its own body shows.
                    : $"mixin — {contributed.Count} field(s), {mixin.Fields.Count} of its own",
                SortText = $"0_{name}"
            }, string.Join("\n\n", documentation)));
        }

        return items;
    }

    // ═══════════════════════════════════════════════════════════════════
    // Attributes
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// The completion list for an attribute position, or <see langword="null"/> when the cursor is
    /// not in one.
    /// </summary>
    private List<CompletionItem>? AttributeCompletions(string content, string uri, int line, int character)
    {
        var scan = IonCommentScanner.Scan(content);

        if (scan.IsCommentOrString(line, character))
            return null;

        var call = IonAttributeLsp.FindCall(scan, line, character);

        if (call is { IsAttribute: true, IsDeclaration: false })
            return ArgumentCompletions(call, uri);

        if (IonAttributeLsp.AfterAtSign(scan, line, character))
            return NameCompletions(uri, IonAttributeLsp.InferTarget(scan, line, character));

        if (IonAttributeLsp.InTargetClause(scan, line, character))
            return TargetCompletions();

        return null;
    }

    /// <summary>
    /// Attribute names after <c>@</c>, narrowed to the ones whose <c>on</c> clause permits the
    /// position the cursor is in.
    /// </summary>
    /// <remarks>
    /// The filter is skipped entirely when the position cannot be determined — an unknown target is
    /// not the same as "no attribute fits", and a list that silently omits the attribute the author
    /// is reaching for is worse than one that is too long. An attribute that is excluded by its
    /// target would be ION0038 the moment it is written, which is the only reason hiding it is
    /// defensible at all.
    /// </remarks>
    private List<CompletionItem> NameCompletions(string uri, IonAttributeTarget? target)
    {
        var items = new List<CompletionItem>();

        foreach (var declaration in IonAttributeLsp.Declarations(workspace, uri))
        {
            if (target is { } position && !declaration.Allows(position))
                continue;

            var documentation = new List<string>
            {
                $"```ion\n{IonAttributeLsp.DeclarationSignature(declaration)}\n```"
            };

            if (!string.IsNullOrWhiteSpace(declaration.Doc))
                documentation.Add(declaration.Doc!);

            documentation.Add(declaration.IsBuiltin
                ? $"*Builtin, from module `{declaration.Origin}`*"
                : $"*Declared in `{declaration.Origin}`*");

            items.Add(IonLspHelpers.WithDoc(new CompletionItem
            {
                Label = $"@{declaration.Name}",
                // The `@` is already in the buffer, so it must be neither inserted again nor
                // included in the text the client filters against.
                FilterText = declaration.Name,
                InsertText = declaration.Name,
                Kind = CompletionItemKind.Property,
                Detail = declaration.Parameters.Count == 0
                    ? declaration.TargetClause ?? "any target"
                    : $"({declaration.Signature})",
                SortText = $"{(declaration.IsBuiltin ? 1 : 0)}_{declaration.Name}"
            }, string.Join("\n\n", documentation)));
        }

        return items;
    }

    /// <summary>
    /// Parameter names inside <c>@Foo(…)</c>, plus the literal keywords, once a value is expected.
    /// </summary>
    private List<CompletionItem>? ArgumentCompletions(IonCallContext call, string uri)
    {
        var declaration = IonAttributeLsp.Find(workspace, uri, call.Name);

        // Undeclared (ION0005): there are no parameter names to offer, and answering with an empty
        // list would leave the position with no completions at all. Fall through to the general
        // symbol list instead — the author is most likely mid-way through fixing the name.
        if (declaration is null)
            return null;

        // Already past the `name:` of this argument — a value goes here, not another name.
        if (call.ActiveArgumentName is not null)
            return LiteralKeywords();

        var items = new List<CompletionItem>();
        var written = call.ArgumentNames.Where(n => n is not null).ToHashSet(StringComparer.Ordinal);

        for (var i = 0; i < declaration.Parameters.Count; i++)
        {
            var parameter = declaration.Parameters[i];

            // A parameter already supplied — by name anywhere in the list, or positionally before
            // the cursor — cannot be supplied again (ION0036).
            if (written.Contains(parameter.Name) || i < call.PositionalsBefore)
                continue;

            items.Add(new CompletionItem
            {
                Label = $"{parameter.Name}:",
                FilterText = parameter.Name,
                InsertText = $"{parameter.Name}: ",
                Kind = CompletionItemKind.Property,
                Detail = $"{parameter.Type}{(parameter.IsOptional ? " (optional)" : "")}",
                Documentation = IonDocMarkdown.ToMarkupContent(parameter.Doc),
                SortText = $"0_{i:D2}_{parameter.Name}"
            });
        }

        items.AddRange(LiteralKeywords());
        return items;
    }

    /// <summary>
    /// <c>true</c>, <c>false</c> and <c>null</c> — the three literals that are words rather than
    /// punctuation, and so the only ones a completion list can usefully offer.
    /// </summary>
    private static List<CompletionItem> LiteralKeywords() =>
    [
        Literal("true", "Boolean literal"),
        Literal("false", "Boolean literal"),
        Literal("null", "Omits an optional (`T?`) argument explicitly")
    ];

    private static CompletionItem Literal(string word, string detail) => new()
    {
        Label = word,
        Kind = CompletionItemKind.Keyword,
        Detail = detail,
        SortText = $"1_{word}"
    };

    /// <summary>The twelve target keywords, for the <c>on</c> clause of an attribute declaration.</summary>
    private static List<CompletionItem> TargetCompletions() =>
        IonAttributeTargets.Keywords
            .Select((keyword, index) => new CompletionItem
            {
                Label = keyword,
                Kind = CompletionItemKind.EnumMember,
                Detail = IonAttributeTargets.TryParse(keyword, out var target) ? target.Describe() : null,
                // Declaration order, not alphabetical: it groups the related positions (`enum`
                // next to `enumMember`, `union` next to `unionCase`).
                SortText = $"{index:D2}_{keyword}"
            })
            .ToList();

    private List<CompletionItem> GetModuleNameCompletions(string filePath)
    {
        var modules = workspace.GetExternalModulesForFile(filePath);
        return modules
            .Where(m => m.SourceModule is not null)
            .GroupBy(m => m.SourceModule!)
            .Select(g => IonLspHelpers.WithDoc(new CompletionItem
            {
                Label = g.Key,
                Kind = CompletionItemKind.Module,
                Detail = "External module"
            }, g.Select(m => m.Doc).FirstOrDefault(d => !string.IsNullOrWhiteSpace(d))))
            .ToList();
    }

    private List<CompletionItem> GetModuleTypeCompletions(string filePath, string moduleName)
    {
        var modules = workspace.GetExternalModulesForFile(filePath);
        return modules
            .Where(m => m.SourceModule == moduleName)
            .SelectMany(m => m.Definitions)
            .Select(d => IonLspHelpers.WithDoc(new CompletionItem
            {
                Label = d.name.Identifier,
                Kind = CompletionItemKind.Class,
                Detail = $"from \"{moduleName}\""
            }, d.Doc))
            .ToList();
    }

    private List<CompletionItem> GetAllExternalTypeCompletions(string filePath)
    {
        var modules = workspace.GetExternalModulesForFile(filePath);
        return modules
            .Where(m => m.SourceModule is not null)
            .SelectMany(m => m.Definitions.Select(d => (Module: m.SourceModule!, Type: d)))
            .Select(x => IonLspHelpers.WithDoc(new CompletionItem
            {
                Label = x.Type.name.Identifier,
                Kind = CompletionItemKind.Class,
                Detail = $"from \"{x.Module}\""
            }, x.Type.Doc))
            .ToList();
    }

    private static string? ExtractModuleNameFromLine(string lineText)
    {
        // Try to find: from "moduleName"
        var fromIdx = lineText.IndexOf("from", StringComparison.Ordinal);
        if (fromIdx < 0) return null;

        var afterFrom = lineText[(fromIdx + 4)..].Trim();
        if (afterFrom.Length < 3 || afterFrom[0] != '"') return null;

        var endQuote = afterFrom.IndexOf('"', 1);
        if (endQuote < 0) return null;

        return afterFrom[1..endQuote];
    }

    public override Task<CompletionItem> Handle(CompletionItem request, CancellationToken cancellationToken)
    {
        return Task.FromResult(request);
    }
}
