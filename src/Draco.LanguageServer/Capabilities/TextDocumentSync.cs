using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Draco.Compiler.Api.Syntax;
using Draco.Lsp.Model;
using Draco.Lsp.Server.TextDocument;

namespace Draco.LanguageServer;

internal sealed partial class DracoLanguageServer : ITextDocumentSync
{
    public async Task TextDocumentDidOpenAsync(DidOpenTextDocumentParams param, CancellationToken cancellationToken)
    {
        var uri = param.TextDocument.Uri;
        var sourceText = param.TextDocument.Text;
        this.UpdateDocument(uri, sourceText);
        await this.PublishDiagnosticsAsync(uri);
    }

    public Task TextDocumentDidCloseAsync(DidCloseTextDocumentParams param, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public async Task TextDocumentDidChangeAsync(DidChangeTextDocumentParams param, CancellationToken cancellationToken)
    {
        var uri = param.TextDocument.Uri;
        var change = param.ContentChanges.First();
        var sourceText = change.Text;
        this.UpdateDocument(uri, sourceText);
        await this.PublishDiagnosticsAsync(uri);
    }

    private void UpdateDocument(DocumentUri documentUri, string? sourceText = null)
    {
        var newSourceText = sourceText is null
            ? this.documentRepository.GetOrCreateDocument(documentUri)
            : this.documentRepository.AddOrUpdateDocument(documentUri, sourceText);
        var uri = documentUri.ToUri();
        var oldTree = this.compilation.SyntaxTrees
            .FirstOrDefault(tree => tree.SourceText.Path == uri);
        this.syntaxTree = SyntaxTree.Parse(newSourceText);
        this.compilation = this.compilation.UpdateSyntaxTree(oldTree, this.syntaxTree);
        this.semanticModel = this.compilation.GetSemanticModel(this.syntaxTree);
    }

    private async Task PublishDiagnosticsAsync(DocumentUri uri)
    {
        var diags = this.semanticModel.Diagnostics;
        var lspDiags = diags.Select(Translator.ToLsp).ToList();
        await this.client.PublishDiagnosticsAsync(new()
        {
            Uri = uri,
            Diagnostics = lspDiags,
        });
    }
}