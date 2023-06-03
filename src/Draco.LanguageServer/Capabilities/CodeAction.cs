using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Draco.Lsp.Model;
using Draco.Lsp.Server.Language;

namespace Draco.LanguageServer;

internal sealed partial class DracoLanguageServer : ICodeAction
{
    public CodeActionRegistrationOptions CodeActionRegistrationOptions => new()
    {
        DocumentSelector = this.DocumentSelector,
        CodeActionKinds = new[] { CodeActionKind.QuickFix },
        ResolveProvider = false
    };

    public Task<IList<OneOf<Command, CodeAction>>?> CodeActionAsync(CodeActionParams param, CancellationToken cancellationToken)
    {
        var fixes = this.codeFixService.GetCodeFixes(this.syntaxTree, this.semanticModel, Translator.ToCompiler(param.Range));
        var actions = new List<OneOf<Command, CodeAction>>();

        foreach (var fix in fixes)
        {
            actions.Add(new CodeAction()
            {
                Title = fix.DisplayText,
                //TODO: we might have some other fixes in future
                Kind = CodeActionKind.QuickFix,
                Edit = new WorkspaceEdit()
                {
                    Changes = new Dictionary<DocumentUri, IList<Lsp.Model.TextEdit>>()
                    {
                        { param.TextDocument.Uri, fix.Edits.Select(x => Translator.ToLsp(x)).ToList() }
                    }
                }
            });
        }
        return Task.FromResult<IList<OneOf<Command, CodeAction>>?>(actions);
    }
}