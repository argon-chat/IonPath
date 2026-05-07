namespace ion.compiler.Lsp;

using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ion.syntax;

public class IonSignatureHelpHandler(IonWorkspace workspace) : SignatureHelpHandlerBase
{
    protected override SignatureHelpRegistrationOptions CreateRegistrationOptions(
        SignatureHelpCapability capability, ClientCapabilities clientCapabilities)
    {
        return new SignatureHelpRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("ion"),
            TriggerCharacters = new Container<string>("(", ",")
        };
    }

    public override Task<SignatureHelp?> Handle(SignatureHelpParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri.GetFileSystemPath();
        var content = workspace.GetDocumentContent(uri)
            ?? (File.Exists(uri) ? File.ReadAllText(uri) : null);

        if (content is null)
            return Task.FromResult<SignatureHelp?>(null);

        var line = (int)request.Position.Line;
        var col = (int)request.Position.Character;
        var lines = content.Split('\n');
        if (line < 0 || line >= lines.Length)
            return Task.FromResult<SignatureHelp?>(null);

        var lineText = lines[line].TrimEnd('\r');

        // Find if we're inside parentheses of a service method or attribute definition
        var parenPos = FindOpenParen(lineText, col);
        if (parenPos < 0)
            return Task.FromResult<SignatureHelp?>(null);

        // Get the word before the opening paren
        var nameEnd = parenPos;
        while (nameEnd > 0 && lineText[nameEnd - 1] == ' ')
            nameEnd--;
        var nameStart = nameEnd;
        while (nameStart > 0 && IonLspHelpers.IsWordChar(lineText[nameStart - 1]))
            nameStart--;

        if (nameStart >= nameEnd)
            return Task.FromResult<SignatureHelp?>(null);

        var name = lineText[nameStart..nameEnd];

        // Count active parameter (commas before cursor)
        var activeParam = 0;
        for (var i = parenPos + 1; i < col && i < lineText.Length; i++)
            if (lineText[i] == ',') activeParam++;

        // Search for matching service or attribute definition
        var signatures = new List<SignatureInformation>();

        foreach (var file in workspace.ParsedFiles)
        {
            // Service methods
            foreach (var svc in file.serviceSyntaxes)
            {
                foreach (var method in svc.Methods)
                {
                    if (!method.methodName.Identifier.Equals(name, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var parameters = method.arguments.Select(a => new ParameterInformation
                    {
                        Label = new ParameterInformationLabel($"{a.argName.Identifier}: {a.type.Name.Identifier}"),
                        Documentation = $"Type: `{a.type.Name.Identifier}`"
                    }).ToList();

                    var args = string.Join(", ", method.arguments.Select(a =>
                        $"{a.argName.Identifier}: {a.type.Name.Identifier}"));
                    var ret = method.returnType is not null ? $": {method.returnType.Name.Identifier}" : "";

                    signatures.Add(new SignatureInformation
                    {
                        Label = $"{method.methodName.Identifier}({args}){ret}",
                        Documentation = $"Method of service `{svc.serviceName.Identifier}`",
                        Parameters = new Container<ParameterInformation>(parameters)
                    });
                }

                // Service base arguments (constructor)
                if (svc.serviceName.Identifier.Equals(name, StringComparison.OrdinalIgnoreCase)
                    && svc.BaseArguments.Count > 0)
                {
                    var parameters = svc.BaseArguments.Select(a => new ParameterInformation
                    {
                        Label = new ParameterInformationLabel($"{a.argName.Identifier}: {a.type.Name.Identifier}"),
                        Documentation = $"Type: `{a.type.Name.Identifier}`"
                    }).ToList();

                    var args = string.Join(", ", svc.BaseArguments.Select(a =>
                        $"{a.argName.Identifier}: {a.type.Name.Identifier}"));

                    signatures.Add(new SignatureInformation
                    {
                        Label = $"service {svc.serviceName.Identifier}({args})",
                        Documentation = "Service base arguments",
                        Parameters = new Container<ParameterInformation>(parameters)
                    });
                }
            }

            // Attribute definitions
            foreach (var attr in file.attributeDefSyntaxes)
            {
                if (!attr.Name.Identifier.Equals(name, StringComparison.OrdinalIgnoreCase))
                    continue;

                var parameters = attr.Args.Select(a => new ParameterInformation
                {
                    Label = new ParameterInformationLabel($"{a.argName.Identifier}: {a.type.Name.Identifier}"),
                    Documentation = $"Type: `{a.type.Name.Identifier}`"
                }).ToList();

                var args = string.Join(", ", attr.Args.Select(a =>
                    $"{a.argName.Identifier}: {a.type.Name.Identifier}"));

                signatures.Add(new SignatureInformation
                {
                    Label = $"@{attr.Name.Identifier}({args})",
                    Documentation = "Attribute",
                    Parameters = new Container<ParameterInformation>(parameters)
                });
            }
        }

        if (signatures.Count == 0)
            return Task.FromResult<SignatureHelp?>(null);

        return Task.FromResult<SignatureHelp?>(new SignatureHelp
        {
            Signatures = new Container<SignatureInformation>(signatures),
            ActiveSignature = 0,
            ActiveParameter = activeParam
        });
    }

    private static int FindOpenParen(string line, int col)
    {
        var depth = 0;
        for (var i = Math.Min(col, line.Length) - 1; i >= 0; i--)
        {
            if (line[i] == ')') depth++;
            else if (line[i] == '(')
            {
                if (depth == 0) return i;
                depth--;
            }
        }
        return -1;
    }
}
