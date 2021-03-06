using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using HotPathAllocationAnalyzer.Helpers;
using HotPathAllocationAnalyzer.Support;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace HotPathAllocationAnalyzer.Analyzers
{
    
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public class MethodCallAnalyzer : AllocationAnalyzer
    {
        private string _whitelistFilePath;
        private readonly HashSet<string> _whitelistedMethods = new HashSet<string>();
        
        public static readonly DiagnosticDescriptor ExternalMethodCallRule = new DiagnosticDescriptor("HAA0701", "Unsafe method call", $"All method call from here should be marked as {nameof(NoAllocation)} or whitelisted {{0}}", "Performance", DiagnosticSeverity.Error, true);
        public static readonly DiagnosticDescriptor UnsafePropertyAccessRule = new DiagnosticDescriptor("HAA0702", "Unsafe property access", $"All property access from here should be marked as {nameof(NoAllocation)} or whitelisted {{0}}", "Performance", DiagnosticSeverity.Error, true);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(ExternalMethodCallRule, UnsafePropertyAccessRule);
        
        protected override SyntaxKind[] Expressions => new[] { SyntaxKind.InvocationExpression, SyntaxKind.SimpleMemberAccessExpression };
        
        private static readonly object[] EmptyMessageArgs = { };

        public void AddToWhiteList(string method)
        {
            _whitelistedMethods.Add(method);
        }

        public override void Initialize(AnalysisContext context)
        {
            base.Initialize(context);

            context.RegisterCompilationStartAction(analysisContext =>
            {
                FindWhiteListPath(analysisContext);

                if (_whitelistFilePath != null && File.Exists(_whitelistFilePath))
                    _whitelistedMethods.UnionWith(File.ReadAllLines(_whitelistFilePath));
            });
        }

        private void FindWhiteListPath(CompilationStartAnalysisContext analysisContext)
        {
            if (_whitelistFilePath != null)
                return;
            
            var lineSpans = analysisContext.Compilation.Assembly.Locations
                                           .Select(l => l.GetLineSpan())
                                           .Where(lp => lp.IsValid)
                                           .Where(lp => !string.IsNullOrEmpty(lp.Path));

            foreach (var lineSpan in lineSpans)
            {
                var configurationDirectory = ConfigurationHelper.FindConfigurationDirectory(lineSpan.Path);
                if (configurationDirectory == null)
                    continue;

                _whitelistFilePath = Path.Combine(configurationDirectory, AllocationRules.WhitelistFileName);
                if (File.Exists(_whitelistFilePath))
                    break;
            }
        }

        protected override void AnalyzeNode(SyntaxNodeAnalysisContext context)
        {
            var semanticModel = context.SemanticModel;
            var cancellationToken = context.CancellationToken;

            if (context.Node is InvocationExpressionSyntax invocationExpression && semanticModel.GetSymbolInfo(invocationExpression, cancellationToken).Symbol is IMethodSymbol methodInfo)
            {
                if (!AttributeHelper.HasNoAllocationAttribute(methodInfo)
                    && !AttributeHelper.HasIgnoreAllocationAttribute(methodInfo)
                    && !IsWhitelisted(methodInfo)
                    && !IsInSafeScope(semanticModel, invocationExpression))
                {
                    ReportError(context, invocationExpression, MethodSymbolSerializer.Serialize(methodInfo), ExternalMethodCallRule);
                }
            }

            if (context.Node is MemberAccessExpressionSyntax memberAccessExpression && semanticModel.GetSymbolInfo(memberAccessExpression, cancellationToken).Symbol is IPropertySymbol propertyInfo)
            {
                if (!AttributeHelper.HasNoAllocationAttribute(propertyInfo)
                    && !AttributeHelper.HasNoAllocationAttribute(propertyInfo.GetMethod)
                    && !AttributeHelper.HasIgnoreAllocationAttribute(propertyInfo)
                    && !AttributeHelper.HasIgnoreAllocationAttribute(propertyInfo.GetMethod)
                    && !IsAutoProperty(context, propertyInfo)
                    && !IsWhitelisted(propertyInfo)
                    && !IsInSafeScope(semanticModel, memberAccessExpression))
                {
                    ReportError(context, memberAccessExpression, MethodSymbolSerializer.Serialize(propertyInfo), UnsafePropertyAccessRule);
                }
            }
        }

        private bool IsWhitelisted(IMethodSymbol methodInfo)
        {
            return _whitelistedMethods.Contains(MethodSymbolSerializer.Serialize(methodInfo));
        }

        private bool IsWhitelisted(IPropertySymbol methodInfo)
        {
            return _whitelistedMethods.Contains(MethodSymbolSerializer.Serialize(methodInfo));
        }

        private static bool IsAutoProperty(SyntaxNodeAnalysisContext context, IPropertySymbol propertyInfo)
        {
            var name = propertyInfo.Name;
            var fields = propertyInfo.ContainingType.GetMembers()
                                     .Where(x => x.Name.Contains($"<{name}>"));
            
            return fields.Any() || (propertyInfo.GetMethod?.GetAttributes().Any(AllocationRules.IsCompilerGeneratedAttribute) ?? false);
        }

        private static bool IsInSafeScope(SemanticModel semanticModel, SyntaxNode symbol)
        {
            if (symbol == null)
                return false;
            
            if (symbol.Parent is UsingStatementSyntax usingStatement && usingStatement.Expression is ObjectCreationExpressionSyntax creationExpressionSyntax)
            {
                var type = semanticModel.GetTypeInfo(creationExpressionSyntax).Type;
                if (IsSafeScopeType(type))
                    return true;
            }
            
            if (symbol.Parent is BlockSyntax blockSyntax)
            {
                var usingStatements = blockSyntax.Statements
                                                 .TakeWhile(x => !x.Equals(symbol))
                                                 .OfType<LocalDeclarationStatementSyntax>()
                                                 .Select(x => semanticModel.GetTypeInfo(x.Declaration.Type).Type)
                                                 .ToArray();
                
                if (usingStatements.Any(IsSafeScopeType))
                    return true;
            }

            return IsInSafeScope(semanticModel, symbol.Parent);
        }

        private static bool IsSafeScopeType(ITypeSymbol? type)
        {
            return type != null
                   && type.Name == nameof(AllocationFreeScope)
                   && type.ContainingNamespace.ToDisplayString() == typeof(AllocationFreeScope).Namespace;
        }

        private void ReportError(SyntaxNodeAnalysisContext context, SyntaxNode node, string name, DiagnosticDescriptor externalMethodCallRule)
        {
            string details;
            if (string.IsNullOrWhiteSpace(_whitelistFilePath))
                details = "(no whitelist found)";
            else if (!File.Exists(_whitelistFilePath))
                details = $"(whitelist path is invalid: '{_whitelistFilePath}')";
            else if (_whitelistedMethods.Count == 0)
                details = $"(whitelist is empty at path: '{_whitelistFilePath}')";
            else 
                details = $"({node} / {name})";
            
            context.ReportDiagnostic(Diagnostic.Create(externalMethodCallRule, node.GetLocation(), details));
            HeapAllocationAnalyzerEventSource.Logger.PossiblyAllocatingMethodCall(node.SyntaxTree.FilePath);
        }
    }
}
