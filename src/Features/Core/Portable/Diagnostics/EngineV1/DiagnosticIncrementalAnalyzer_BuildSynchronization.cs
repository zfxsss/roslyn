﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Diagnostics.EngineV1
{
    internal partial class DiagnosticIncrementalAnalyzer
    {
        public override async Task SynchronizeWithBuildAsync(Workspace workspace, ImmutableDictionary<ProjectId, ImmutableArray<DiagnosticData>> map)
        {
            if (!PreferBuildErrors(workspace))
            {
                // prefer live errors over build errors
                return;
            }

            var solution = workspace.CurrentSolution;
            foreach (var projectEntry in map)
            {
                var project = solution.GetProject(projectEntry.Key);
                if (project == null)
                {
                    continue;
                }

                var stateSets = _stateManager.CreateBuildOnlyProjectStateSet(project);
                var lookup = projectEntry.Value.ToLookup(d => d.DocumentId);

                // do project one first
                await SynchronizeWithBuildAsync(project, stateSets, lookup[null]).ConfigureAwait(false);

                foreach (var document in project.Documents)
                {
                    await SynchronizeWithBuildAsync(document, stateSets, lookup[document.Id]).ConfigureAwait(false);
                }
            }
        }

        private async Task SynchronizeWithBuildAsync(Project project, IEnumerable<StateSet> stateSets, IEnumerable<DiagnosticData> diagnostics)
        {
            using (var poolObject = SharedPools.Default<HashSet<string>>().GetPooledObject())
            {
                var lookup = CreateDiagnosticIdLookup(diagnostics);
                foreach (var stateSet in stateSets)
                {
                    var descriptors = HostAnalyzerManager.GetDiagnosticDescriptors(stateSet.Analyzer);
                    var liveDiagnostics = ConvertToLiveDiagnostics(lookup, descriptors, poolObject.Object);

                    // we are using Default so that things like LB can't use cached information
                    var projectTextVersion = VersionStamp.Default;
                    var semanticVersion = await project.GetDependentSemanticVersionAsync(CancellationToken.None).ConfigureAwait(false);

                    var state = stateSet.GetState(StateType.Project);
                    var existingDiagnostics = await state.TryGetExistingDataAsync(project, CancellationToken.None).ConfigureAwait(false);

                    var mergedDiagnostics = MergeDiagnostics(liveDiagnostics, GetExistingDiagnostics(existingDiagnostics));
                    await state.PersistAsync(project, new AnalysisData(projectTextVersion, semanticVersion, mergedDiagnostics), CancellationToken.None).ConfigureAwait(false);
                    RaiseDiagnosticsCreated(StateType.Project, project.Id, stateSet, new SolutionArgument(project), mergedDiagnostics);
                }
            }
        }

        private async Task SynchronizeWithBuildAsync(Document document, IEnumerable<StateSet> stateSets, IEnumerable<DiagnosticData> diagnostics)
        {
            var workspace = document.Project.Solution.Workspace;

            // check whether, for opened documents, we want to prefer live diagnostics
            if (PreferLiveErrorsOnOpenedFiles(workspace) && workspace.IsDocumentOpen(document.Id))
            {
                // enqueue re-analysis of open documents.
                this.Owner.Reanalyze(workspace, documentIds: SpecializedCollections.SingletonEnumerable(document.Id), highPriority: true);
                return;
            }

            using (var poolObject = SharedPools.Default<HashSet<string>>().GetPooledObject())
            {
                var lookup = CreateDiagnosticIdLookup(diagnostics);

                foreach (var stateSet in stateSets)
                {
                    // we are using Default so that things like LB can't use cached information
                    var textVersion = VersionStamp.Default;
                    var semanticVersion = await document.Project.GetDependentSemanticVersionAsync(CancellationToken.None).ConfigureAwait(false);

                    // clear document and project live errors
                    await PersistAndReportAsync(stateSet, StateType.Project, document, textVersion, semanticVersion, ImmutableArray<DiagnosticData>.Empty).ConfigureAwait(false);
                    await PersistAndReportAsync(stateSet, StateType.Syntax, document, textVersion, semanticVersion, ImmutableArray<DiagnosticData>.Empty).ConfigureAwait(false);

                    var descriptors = HostAnalyzerManager.GetDiagnosticDescriptors(stateSet.Analyzer);
                    var liveDiagnostics = ConvertToLiveDiagnostics(lookup, descriptors, poolObject.Object);

                    // REVIEW: for now, we are putting build error in document state rather than creating its own state.
                    //         reason is so that live error can take over it as soon as possible
                    //         this also means there can be slight race where it will clean up eventually. if live analysis runs for syntax but didn't run
                    //         for document yet, then we can have duplicated entries in the error list until live analysis catch.
                    await PersistAndReportAsync(stateSet, StateType.Document, document, textVersion, semanticVersion, liveDiagnostics).ConfigureAwait(false);
                }
            }
        }

        private bool PreferBuildErrors(Workspace workspace)
        {
            return workspace.Options.GetOption(InternalDiagnosticsOptions.BuildErrorIsTheGod) || workspace.Options.GetOption(InternalDiagnosticsOptions.PreferBuildErrorsOverLiveErrors);
        }

        private bool PreferLiveErrorsOnOpenedFiles(Workspace workspace)
        {
            return !workspace.Options.GetOption(InternalDiagnosticsOptions.BuildErrorIsTheGod) && workspace.Options.GetOption(InternalDiagnosticsOptions.PreferLiveErrorsOnOpenedFiles);
        }

        private async Task PersistAndReportAsync(
            StateSet stateSet, StateType stateType, Document document, VersionStamp textVersion, VersionStamp semanticVersion, ImmutableArray<DiagnosticData> diagnostics)
        {
            var state = stateSet.GetState(stateType);
            var existingDiagnostics = await state.TryGetExistingDataAsync(document, CancellationToken.None).ConfigureAwait(false);

            var mergedDiagnostics = MergeDiagnostics(diagnostics, GetExistingDiagnostics(existingDiagnostics));
            await state.PersistAsync(document, new AnalysisData(textVersion, semanticVersion, mergedDiagnostics), CancellationToken.None).ConfigureAwait(false);
            RaiseDiagnosticsCreated(stateType, document.Id, stateSet, new SolutionArgument(document), mergedDiagnostics);
        }

        private static ImmutableArray<DiagnosticData> GetExistingDiagnostics(AnalysisData analysisData)
        {
            if (analysisData == null)
            {
                return ImmutableArray<DiagnosticData>.Empty;
            }

            return analysisData.Items;
        }

        private ImmutableArray<DiagnosticData> MergeDiagnostics(ImmutableArray<DiagnosticData> liveDiagnostics, ImmutableArray<DiagnosticData> existingDiagnostics)
        {
            ImmutableArray<DiagnosticData>.Builder builder = null;

            if (liveDiagnostics.Length > 0)
            {
                builder = ImmutableArray.CreateBuilder<DiagnosticData>();
                builder.AddRange(liveDiagnostics);
            }

            if (existingDiagnostics.Length > 0)
            {
                // retain hidden live diagnostics since it won't be comes from build.
                builder = builder ?? ImmutableArray.CreateBuilder<DiagnosticData>();
                builder.AddRange(existingDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Hidden));
            }

            return builder == null ? ImmutableArray<DiagnosticData>.Empty : builder.ToImmutable();
        }

        private static ILookup<string, DiagnosticData> CreateDiagnosticIdLookup(IEnumerable<DiagnosticData> diagnostics)
        {
            return diagnostics.ToLookup(d => d.Id);
        }

        private ImmutableArray<DiagnosticData> ConvertToLiveDiagnostics(
            ILookup<string, DiagnosticData> lookup, ImmutableArray<DiagnosticDescriptor> descriptors, HashSet<string> seen)
        {
            if (lookup == null)
            {
                return ImmutableArray<DiagnosticData>.Empty;
            }

            ImmutableArray<DiagnosticData>.Builder builder = null;
            foreach (var descriptor in descriptors)
            {
                // make sure we don't report same id to multiple different analyzers
                if (!seen.Add(descriptor.Id))
                {
                    // TODO: once we have information where diagnostic came from, we probably don't need this.
                    continue;
                }

                var items = lookup[descriptor.Id];
                if (items == null)
                {
                    continue;
                }

                builder = builder ?? ImmutableArray.CreateBuilder<DiagnosticData>();
                builder.AddRange(items.Select(d => CreateLiveDiagnostic(descriptor, d)));
            }

            return builder == null ? ImmutableArray<DiagnosticData>.Empty : builder.ToImmutable();
        }

        private static DiagnosticData CreateLiveDiagnostic(DiagnosticDescriptor descriptor, DiagnosticData diagnostic)
        {
            return new DiagnosticData(
                descriptor.Id,
                descriptor.Category,
                diagnostic.Message,
                descriptor.GetBingHelpMessage(),
                diagnostic.Severity,
                descriptor.DefaultSeverity,
                descriptor.IsEnabledByDefault,
                diagnostic.WarningLevel,
                descriptor.CustomTags.ToImmutableArray(),
                diagnostic.Properties,
                diagnostic.Workspace,
                diagnostic.ProjectId,
                diagnostic.DataLocation,
                diagnostic.AdditionalLocations,
                descriptor.Title.ToString(CultureInfo.CurrentUICulture),
                descriptor.Description.ToString(CultureInfo.CurrentUICulture),
                descriptor.HelpLinkUri,
                isSuppressed: diagnostic.IsSuppressed);
        }
    }
}
