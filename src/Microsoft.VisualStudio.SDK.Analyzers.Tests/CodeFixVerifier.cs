﻿// Copyright (c) Microsoft Corporation. All rights reserved.

namespace Microsoft.VisualStudio.SDK.Analyzers.Tests
{
    using System;
    using System.Collections.Generic;
    using System.Collections.Immutable;
    using System.Linq;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.CodeActions;
    using Microsoft.CodeAnalysis.CodeFixes;
    using Microsoft.CodeAnalysis.Diagnostics;
    using Microsoft.CodeAnalysis.Formatting;
    using Microsoft.CodeAnalysis.Simplification;
    using Xunit;
    using Xunit.Abstractions;

    public abstract class CodeFixVerifier : DiagnosticVerifier
    {
        protected CodeFixVerifier(ITestOutputHelper logger)
        : base(logger)
        {
        }

        /// <summary>
        /// The set of diagnostics expected after applying a code fix.
        /// </summary>
        public enum PostFixDiagnostics
        {
            /// <summary>
            /// No diagnostics are expected.
            /// </summary>
            None,

            /// <summary>
            /// Diagnostics are allowed, so long as they existed before the fix was applied as well.
            /// </summary>
            Preexisting,

            /// <summary>
            /// We allow any diagnostics, including new ones.
            /// </summary>
            New,
        }

        /// <summary>
        /// Returns the codefix being tested (C#) - to be implemented in non-abstract class
        /// </summary>
        /// <returns>The CodeFixProvider to be used for CSharp code</returns>
        protected abstract CodeFixProvider GetCSharpCodeFixProvider();

        /// <summary>
        /// Called to test a C# codefix when applied on the inputted string as a source
        /// </summary>
        /// <param name="oldSource">A class in the form of a string before the CodeFix was applied to it</param>
        /// <param name="newSource">A class in the form of a string after the CodeFix was applied to it</param>
        /// <param name="codeFixIndex">Index determining which codefix to apply if there are multiple</param>
        /// <param name="expectedPostFixDiagnostics">A bool controlling whether or not the test will fail if the CodeFix introduces other warnings after being applied</param>
        /// <param name="hasEntrypoint"><c>true</c> to set the compiler in a mode as if it were compiling an exe (as opposed to a dll).</param>
        protected void VerifyCSharpFix(string oldSource, string newSource, int? codeFixIndex = null, PostFixDiagnostics expectedPostFixDiagnostics = PostFixDiagnostics.None, bool hasEntrypoint = false)
        {
            this.VerifyFix(LanguageNames.CSharp, this.GetCSharpDiagnosticAnalyzer(), this.GetCSharpCodeFixProvider(), new[] { oldSource }, new[] { newSource }, codeFixIndex, expectedPostFixDiagnostics, hasEntrypoint);
        }

        /// <summary>
        /// Called to test a C# codefix when applied on the inputted string as a source
        /// </summary>
        /// <param name="oldSources">Code files, each in the form of a string before the CodeFix was applied to it</param>
        /// <param name="newSources">Code files, each in the form of a string after the CodeFix was applied to it</param>
        /// <param name="codeFixIndex">Index determining which codefix to apply if there are multiple</param>
        /// <param name="expectedPostFixDiagnostics">The set of diagnostics that are expected to exist after any fix(es) are applied to the original code.</param>
        /// <param name="hasEntrypoint"><c>true</c> to set the compiler in a mode as if it were compiling an exe (as opposed to a dll).</param>
        protected void VerifyCSharpFix(string[] oldSources, string[] newSources, int? codeFixIndex = null, PostFixDiagnostics expectedPostFixDiagnostics = PostFixDiagnostics.None, bool hasEntrypoint = false)
        {
            this.VerifyFix(LanguageNames.CSharp, this.GetCSharpDiagnosticAnalyzer(), this.GetCSharpCodeFixProvider(), oldSources, newSources, codeFixIndex, expectedPostFixDiagnostics, hasEntrypoint);
        }

        protected void VerifyNoCSharpFixOffered(string oldSource, bool hasEntrypoint = false)
        {
            this.VerifyNoFixOffered(LanguageNames.CSharp, this.GetCSharpDiagnosticAnalyzer(), this.GetCSharpCodeFixProvider(), oldSource, hasEntrypoint);
        }

        /// <summary>
        /// Get the existing compiler diagnostics on the inputted document.
        /// </summary>
        /// <param name="document">The Document to run the compiler diagnostic analyzers on</param>
        /// <returns>The compiler diagnostics that were found in the code</returns>
        private static IEnumerable<Diagnostic> GetCompilerDiagnostics(Document document)
        {
            return document.GetSemanticModelAsync().Result.GetDiagnostics();
        }

        /// <summary>
        /// Compare two collections of Diagnostics, and return a list of any new diagnostics that appear only in the second collection.
        /// Note: Considers Diagnostics to be the same if they have the same Ids.  In the case of multiple diagnostics with the same Id in a row,
        /// this method may not necessarily return the new one.
        /// </summary>
        /// <param name="diagnostics">The Diagnostics that existed in the code before the CodeFix was applied</param>
        /// <param name="newDiagnostics">The Diagnostics that exist in the code after the CodeFix was applied</param>
        /// <returns>A list of Diagnostics that only surfaced in the code after the CodeFix was applied</returns>
        private static IEnumerable<Diagnostic> GetNewDiagnostics(IEnumerable<Diagnostic> diagnostics, IEnumerable<Diagnostic> newDiagnostics)
        {
            var oldArray = diagnostics.OrderBy(d => d.Location.SourceSpan.Start).ToArray();
            var newArray = newDiagnostics.OrderBy(d => d.Location.SourceSpan.Start).ToArray();

            int oldIndex = 0;
            int newIndex = 0;

            while (newIndex < newArray.Length)
            {
                if (oldIndex < oldArray.Length && oldArray[oldIndex].Id == newArray[newIndex].Id)
                {
                    ++oldIndex;
                    ++newIndex;
                }
                else
                {
                    yield return newArray[newIndex++];
                }
            }
        }

        /// <summary>
        /// Given a Document, turn it into a string based on the syntax root
        /// </summary>
        /// <param name="document">The Document to be converted to a string</param>
        /// <returns>A string containing the syntax of the Document after formatting</returns>
        private static string GetStringFromDocument(Document document)
        {
            var simplifiedDoc = Simplifier.ReduceAsync(document, Simplifier.Annotation).Result;
            var root = simplifiedDoc.GetSyntaxRootAsync().Result;
            root = Formatter.Format(root, Formatter.Annotation, simplifiedDoc.Project.Solution.Workspace);
            return root.GetText().ToString();
        }

        /// <summary>
        /// Apply the inputted CodeAction to the inputted document.
        /// Meant to be used to apply codefixes.
        /// </summary>
        /// <param name="document">The Document to apply the fix on</param>
        /// <param name="codeAction">A CodeAction that will be applied to the Document.</param>
        /// <returns>A Document with the changes from the CodeAction</returns>
        private static Document ApplyFix(Document document, CodeAction codeAction)
        {
            var operations = codeAction.GetOperationsAsync(CancellationToken.None).GetAwaiter().GetResult();
            var solution = operations.OfType<ApplyChangesOperation>().Single().ChangedSolution;
            return solution.GetDocument(document.Id);
        }

        private void VerifyNoFixOffered(string language, DiagnosticAnalyzer analyzer, CodeFixProvider codeFixProvider, string source, bool hasEntrypoint)
        {
            var document = CreateDocument(source, language, hasEntrypoint);
            var analyzerDiagnostics = GetSortedDiagnosticsFromDocuments(ImmutableArray.Create(analyzer), new[] { document });
            var compilerDiagnostics = GetCompilerDiagnostics(document);
            var attempts = analyzerDiagnostics.Length;

            for (int i = 0; i < attempts; ++i)
            {
                var actions = new List<CodeAction>();
                var context = new CodeFixContext(document, analyzerDiagnostics[0], (a, d) => actions.Add(a), CancellationToken.None);
                codeFixProvider.RegisterCodeFixesAsync(context).Wait();
                Assert.Empty(actions);
            }
        }

        /// <summary>
        /// General verifier for codefixes.
        /// Creates a Document from the source string, then gets diagnostics on it and applies the relevant codefixes.
        /// Then gets the string after the codefix is applied and compares it with the expected result.
        /// Note: If any codefix causes new diagnostics to show up, the test fails unless allowNewCompilerDiagnostics is set to true.
        /// </summary>
        /// <param name="language">The language the source code is in</param>
        /// <param name="analyzer">The analyzer to be applied to the source code</param>
        /// <param name="codeFixProvider">The codefix to be applied to the code wherever the relevant Diagnostic is found</param>
        /// <param name="oldSources">Code files, each in the form of a string before the CodeFix was applied to it</param>
        /// <param name="newSources">Code files, each in the form of a string after the CodeFix was applied to it</param>
        /// <param name="codeFixIndex">Index determining which codefix to apply if there are multiple in the same location</param>
        /// <param name="expectedPostFixDiagnostics">The set of diagnostics that are expected to exist after any fix(es) are applied to the original code.</param>
        /// <param name="hasEntrypoint"><c>true</c> to set the compiler in a mode as if it were compiling an exe (as opposed to a dll).</param>
        private void VerifyFix(string language, DiagnosticAnalyzer analyzer, CodeFixProvider codeFixProvider, string[] oldSources, string[] newSources, int? codeFixIndex, PostFixDiagnostics expectedPostFixDiagnostics, bool hasEntrypoint)
        {
            var project = CreateProject(oldSources, language, hasEntrypoint);
            var analyzerDiagnostics = GetSortedDiagnosticsFromDocuments(ImmutableArray.Create(analyzer), project.Documents.ToArray(), hasEntrypoint);
            var compilerDiagnostics = project.Documents.SelectMany(doc => GetCompilerDiagnostics(doc)).ToList();
            var attempts = analyzerDiagnostics.Length;

            // We'll go through enough for each diagnostic to be caught once
            bool fixApplied = false;
            for (int i = 0; i < attempts; ++i)
            {
                var diagnostic = analyzerDiagnostics[0]; // just get the first one -- the list gets smaller with each loop.
                var document = project.GetDocument(diagnostic.Location.SourceTree);
                var actions = new List<CodeAction>();
                var context = new CodeFixContext(document, diagnostic, (a, d) => actions.Add(a), CancellationToken.None);
                codeFixProvider.RegisterCodeFixesAsync(context).Wait();
                if (!actions.Any())
                {
                    continue;
                }

                document = ApplyFix(document, actions[codeFixIndex ?? 0]);
                fixApplied = true;
                project = document.Project;

                this.Logger.WriteLine("Code after fix:");
                this.LogFileContent(document.GetSyntaxRootAsync().Result.ToFullString());

                analyzerDiagnostics = GetSortedDiagnosticsFromDocuments(ImmutableArray.Create(analyzer), project.Documents.ToArray());
            }

            if (newSources != null && newSources[0] != null)
            {
                Assert.True(fixApplied, "No code fix offered.");

                // After applying all of the code fixes, compare the resulting string to the inputted one
                int j = 0;
                foreach (var document in project.Documents)
                {
                    var actual = GetStringFromDocument(document);
                    Assert.Equal(newSources[j++], actual, ignoreLineEndingDifferences: true);
                }
            }
            else
            {
                Assert.False(fixApplied, "No code fix expected, but was offered.");
            }

            var postFixDiagnostics = project.Documents.SelectMany(doc => GetCompilerDiagnostics(doc)).Concat(GetSortedDiagnosticsFromDocuments(ImmutableArray.Create(analyzer), project.Documents.ToArray(), hasEntrypoint)).ToList();
            var newCompilerDiagnostics = GetNewDiagnostics(compilerDiagnostics, postFixDiagnostics).ToList();

            IEnumerable<Diagnostic> unexpectedDiagnostics;
            switch (expectedPostFixDiagnostics)
            {
                case PostFixDiagnostics.None:
                    unexpectedDiagnostics = postFixDiagnostics;
                    break;
                case PostFixDiagnostics.Preexisting:
                    unexpectedDiagnostics = newCompilerDiagnostics;
                    break;
                case PostFixDiagnostics.New:
                    unexpectedDiagnostics = Enumerable.Empty<Diagnostic>(); // We don't care what's present.
                    break;
                default:
                    throw new NotSupportedException();
            }

            var expectedDiagnostics = postFixDiagnostics.Except(unexpectedDiagnostics);
            this.Logger.WriteLine("Actual diagnostics:");
            this.Logger.WriteLine("EXPECTED:\r\n{0}", expectedDiagnostics.Any() ? FormatDiagnostics(expectedDiagnostics.ToArray()) : "    NONE.");
            this.Logger.WriteLine("UNEXPECTED:\r\n{0}", unexpectedDiagnostics.Any() ? FormatDiagnostics(unexpectedDiagnostics.ToArray()) : "    NONE.");

            // Check if applying the code fix introduced any new compiler diagnostics
            Assert.Empty(unexpectedDiagnostics);
        }
    }
}
