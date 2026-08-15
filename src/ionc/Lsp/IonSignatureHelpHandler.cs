namespace ion.compiler.Lsp;

using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ion.syntax;

/// <summary>
/// Parameter help inside an argument list: an attribute use (<c>@Retry(|)</c>), a service method
/// declaration, or a service's base argument list.
/// </summary>
/// <remarks>
/// <para>
/// The attribute case is the one that carries its weight. A method declaration shows its own
/// parameter names and types on the line being written, so help there is a convenience; an
/// attribute <em>use</em> shows neither — <c>@Cache(300, "user")</c> is two bare literals — and the
/// declaration is usually in another file.
/// </para>
/// <para>
/// The enclosing call is located by <see cref="IonAttributeLsp.FindCall"/>, which scans the whole
/// document through the comment/string mask rather than the current line. That is what makes an
/// argument list split over several lines work, and what stops a <c>(</c> inside a comment or a
/// string literal from opening a phantom call.
/// </para>
/// </remarks>
public class IonSignatureHelpHandler(IonWorkspace workspace) : SignatureHelpHandlerBase
{
    protected override SignatureHelpRegistrationOptions CreateRegistrationOptions(
        SignatureHelpCapability capability, ClientCapabilities clientCapabilities)
    {
        return new SignatureHelpRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("ion"),
            TriggerCharacters = new Container<string>("(", ",", ":")
        };
    }

    public override Task<SignatureHelp?> Handle(SignatureHelpParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.GetFileSystemPath();
        var content = workspace.GetDocumentContent(uri)
            ?? (File.Exists(uri) ? File.ReadAllText(uri) : null);

        if (content is null)
            return Task.FromResult<SignatureHelp?>(null);

        var scan = IonCommentScanner.Scan(content);
        var call = IonAttributeLsp.FindCall(scan, request.Position.Line, request.Position.Character);

        if (call is null)
            return Task.FromResult<SignatureHelp?>(null);

        return Task.FromResult(call.IsAttribute
            ? AttributeHelp(call, uri)
            : DeclarationHelp(call));
    }

    // ── Attribute uses ─────────────────────────────────────────────────

    private SignatureHelp? AttributeHelp(IonCallContext call, string uri)
    {
        // `attribute @Retry(|)` is the declaration of the parameter list, not a call of it. Its
        // parameters are being written right there, so echoing them back is noise.
        if (call.IsDeclaration)
            return null;

        var declaration = IonAttributeLsp.Find(workspace, uri, call.Name);

        if (declaration is null || declaration.Parameters.Count == 0)
            return null;

        var parameters = declaration.Parameters
            .Select(p => new ParameterInformation
            {
                Label = new ParameterInformationLabel($"{p.Name}: {p.Type}"),
                Documentation = IonDocMarkdown.WithDoc(p.Doc, Describe(p))
            })
            .ToList();

        var signature = new SignatureInformation
        {
            Label = declaration.Label,
            Documentation = IonDocMarkdown.WithDoc(declaration.Doc, Summary(declaration)),
            Parameters = new Container<ParameterInformation>(parameters)
        };

        return new SignatureHelp
        {
            Signatures = new Container<SignatureInformation>(signature),
            ActiveSignature = 0,
            ActiveParameter = ActiveParameter(call, declaration)
        };
    }

    /// <summary>
    /// Which parameter the cursor is on.
    /// </summary>
    /// <remarks>
    /// A named argument goes to its own slot wherever it is written, so the highlight has to follow
    /// the <em>name</em> and not the comma count. An unnamed argument fills the next positional slot,
    /// which is the number of positional arguments before it — not its index in the written list,
    /// because a named argument earlier in the list consumed no positional slot. This mirrors
    /// <c>IonAttributeBinder.Bind</c> exactly, so the highlight can never point at a different
    /// parameter than the one the compiler will bind to.
    /// </remarks>
    private static int? ActiveParameter(IonCallContext call, IonAttributeDeclaration declaration)
    {
        if (call.ActiveArgumentName is { } named)
        {
            var index = declaration.IndexOf(named);

            // An unknown name (ION0035) has no slot to highlight. Highlighting slot 0 would be a
            // confident lie; showing the signature with nothing selected is the honest answer.
            return index < 0 ? null : index;
        }

        return call.PositionalsBefore < declaration.Parameters.Count
            ? call.PositionalsBefore
            : null;
    }

    private static string Describe(IonAttributeParameter parameter)
        => parameter.IsOptional
            ? $"`{parameter.Type}` — optional, may be omitted."
            : $"`{parameter.Type}` — required.";

    private static string Summary(IonAttributeDeclaration declaration)
    {
        var required = declaration.RequiredCount;
        var total = declaration.Parameters.Count;

        var arity = required == total
            ? $"{total} argument(s)."
            : $"{required} required, {total - required} optional argument(s).";

        return declaration.TargetClause is { } clause
            ? $"{arity} Declared `{clause}`."
            : arity;
    }

    // ── Method and service declarations ────────────────────────────────

    private SignatureHelp? DeclarationHelp(IonCallContext call)
    {
        var signatures = new List<SignatureInformation>();

        foreach (var file in workspace.ParsedFiles)
        {
            foreach (var svc in file.serviceSyntaxes)
            {
                foreach (var method in svc.Methods)
                {
                    if (!method.methodName.Identifier.Equals(call.Name, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var args = string.Join(", ", method.arguments.Select(Written));
                    var ret = method.returnType is not null
                        ? $": {IonAttributeLsp.FormatType(method.returnType)}"
                        : "";

                    signatures.Add(new SignatureInformation
                    {
                        Label = $"{method.methodName.Identifier}({args}){ret}",
                        Documentation = IonDocMarkdown.WithDoc(
                            method.Comments, $"Method of service `{svc.serviceName.Identifier}`"),
                        Parameters = new Container<ParameterInformation>(Parameters(method.arguments))
                    });
                }

                if (svc.serviceName.Identifier.Equals(call.Name, StringComparison.OrdinalIgnoreCase)
                    && svc.BaseArguments.Count > 0)
                {
                    var args = string.Join(", ", svc.BaseArguments.Select(Written));

                    signatures.Add(new SignatureInformation
                    {
                        Label = $"service {svc.serviceName.Identifier}({args})",
                        Documentation = IonDocMarkdown.WithDoc(svc.Comments, "Service base arguments"),
                        Parameters = new Container<ParameterInformation>(Parameters(svc.BaseArguments))
                    });
                }
            }
        }

        if (signatures.Count == 0)
            return null;

        return new SignatureHelp
        {
            Signatures = new Container<SignatureInformation>(signatures),
            ActiveSignature = 0,
            ActiveParameter = call.ArgumentIndex
        };
    }

    private static List<ParameterInformation> Parameters(IEnumerable<IonArgumentSyntax> arguments)
        => arguments.Select(a => new ParameterInformation
        {
            Label = new ParameterInformationLabel(Written(a)),
            Documentation = IonDocMarkdown.WithDoc(a.Comments, $"Type: `{IonAttributeLsp.FormatType(a.type)}`")
        }).ToList();

    /// <summary>
    /// One parameter as written. Goes through <see cref="IonAttributeLsp.FormatType"/> rather than
    /// reading <c>type.Name</c>, which dropped every <c>?</c>, <c>[]</c> and <c>~</c> and so
    /// rendered <c>tags: string[]?</c> as <c>tags: string</c>.
    /// </summary>
    private static string Written(IonArgumentSyntax argument)
    {
        var modifier = argument.modifiers == IonArgumentModifiers.None
            ? ""
            : argument.modifiers.ToString().ToLowerInvariant() + " ";

        return $"{modifier}{argument.argName.Identifier}: {IonAttributeLsp.FormatType(argument.type)}";
    }
}
