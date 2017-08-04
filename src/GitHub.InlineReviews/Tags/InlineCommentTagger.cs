﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Tasks;
using GitHub.Extensions;
using GitHub.InlineReviews.Services;
using GitHub.Models;
using GitHub.Services;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Projection;
using Microsoft.VisualStudio.Text.Tagging;
using ReactiveUI;
using System.Collections;
using GitHub.Helpers;

namespace GitHub.InlineReviews.Tags
{
    /// <summary>
    /// Creates tags in an <see cref="ITextBuffer"/> for inline comment threads.
    /// </summary>
    sealed class InlineCommentTagger : ITagger<InlineCommentTag>, IEditorContentSource, IDisposable
    {
        readonly IGitService gitService;
        readonly IGitClient gitClient;
        readonly IDiffService diffService;
        readonly ITextBuffer buffer;
        readonly ITextView view;
        readonly IPullRequestSessionManager sessionManager;
        readonly IInlineCommentPeekService peekService;
        readonly Subject<ITextSnapshot> signalRebuild;
        readonly Dictionary<IInlineCommentThreadModel, ITrackingPoint> trackingPoints;
        readonly int? tabsToSpaces;
        bool initialized;
        ITextDocument document;
        string fullPath;
        string relativePath;
        bool leftHandSide;
        IDisposable managerSubscription;
        IDisposable sessionSubscription;
        IPullRequestSession session;
        IPullRequestSessionFile file;

        public InlineCommentTagger(
            IGitService gitService,
            IGitClient gitClient,
            IDiffService diffService,
            ITextView view,
            ITextBuffer buffer,
            IPullRequestSessionManager sessionManager,
            IInlineCommentPeekService peekService)
        {
            Guard.ArgumentNotNull(gitService, nameof(gitService));
            Guard.ArgumentNotNull(gitClient, nameof(gitClient));
            Guard.ArgumentNotNull(diffService, nameof(diffService));
            Guard.ArgumentNotNull(buffer, nameof(buffer));
            Guard.ArgumentNotNull(sessionManager, nameof(sessionManager));
            Guard.ArgumentNotNull(peekService, nameof(peekService));

            this.gitService = gitService;
            this.gitClient = gitClient;
            this.diffService = diffService;
            this.buffer = buffer;
            this.view = view;
            this.sessionManager = sessionManager;
            this.peekService = peekService;

            trackingPoints = new Dictionary<IInlineCommentThreadModel, ITrackingPoint>();

            if (view.Options.GetOptionValue("Tabs/ConvertTabsToSpaces", false))
            {
                tabsToSpaces = view.Options.GetOptionValue<int?>("Tabs/TabSize", null);
            }

            signalRebuild = new Subject<ITextSnapshot>();
            signalRebuild.Throttle(TimeSpan.FromMilliseconds(500))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(x => Rebuild(x).Forget());

            this.buffer.Changed += Buffer_Changed;
        }

        public bool ShowMargin => file != null;

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        public void Dispose()
        {
            sessionSubscription?.Dispose();
            managerSubscription?.Dispose();
        }

        public IEnumerable<ITagSpan<InlineCommentTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            var result = new List<ITagSpan<InlineCommentTag>>();

            if (!initialized)
            {
                // Sucessful initialization will call NotifyTagsChanged, causing this method to be re-called.
                Initialize();
            }
            else if (file != null)
            {
                foreach (var span in spans)
                {
                    var startLine = span.Start.GetContainingLine().LineNumber;
                    var endLine = span.End.GetContainingLine().LineNumber;
                    var linesWithComments = new BitArray((endLine - startLine) + 1);
                    var spanThreads = file.InlineCommentThreads.Where(x =>
                        x.LineNumber >= startLine &&
                        x.LineNumber <= endLine);

                    foreach (var thread in spanThreads)
                    {
                        var snapshot = span.Snapshot;
                        var line = snapshot.GetLineFromLineNumber(thread.LineNumber);

                        if ((leftHandSide && thread.DiffLineType == DiffChangeType.Delete) ||
                            (!leftHandSide && thread.DiffLineType != DiffChangeType.Delete))
                        {
                            var trackingPoint = snapshot.CreateTrackingPoint(line.Start, PointTrackingMode.Positive);
                            trackingPoints[thread] = trackingPoint;
                            linesWithComments[thread.LineNumber - startLine] = true;

                            result.Add(new TagSpan<ShowInlineCommentTag>(
                                new SnapshotSpan(line.Start, line.End),
                                new ShowInlineCommentTag(session, thread)));
                        }
                    }

                    foreach (var chunk in file.Diff)
                    {
                        foreach (var line in chunk.Lines)
                        {
                            var lineNumber = (leftHandSide ? line.OldLineNumber : line.NewLineNumber) - 1;

                            if (lineNumber >= startLine && 
                                lineNumber <= endLine && 
                                !linesWithComments[lineNumber - startLine]
                                && (!leftHandSide || line.Type == DiffChangeType.Delete))
                            {
                                var snapshotLine = span.Snapshot.GetLineFromLineNumber(lineNumber);
                                result.Add(new TagSpan<InlineCommentTag>(
                                    new SnapshotSpan(snapshotLine.Start, snapshotLine.End),
                                    new AddInlineCommentTag(session, file.CommitSha, relativePath, line.DiffLineNumber, lineNumber, line.Type)));
                            }
                        }
                    }
                }
            }

            return result;
        }

        Task<byte[]> IEditorContentSource.GetContent()
        {
            return Task.FromResult(GetContents(buffer.CurrentSnapshot));
        }

        void Initialize()
        {
            document = buffer.Properties.GetProperty<ITextDocument>(typeof(ITextDocument));

            if (document == null)
                return;

            var bufferInfo = sessionManager.GetTextBufferInfo(buffer);
            IPullRequestSession session = null;

            if (bufferInfo != null)
            {
                fullPath = bufferInfo.FilePath;
                leftHandSide = bufferInfo.IsLeftComparisonBuffer;

                if (!bufferInfo.Session.IsCheckedOut)
                {
                    session = bufferInfo.Session;
                }
            }
            else
            {
                fullPath = document.FilePath;
            }

            if (session == null)
            {
                managerSubscription = sessionManager.WhenAnyValue(x => x.CurrentSession).Subscribe(SessionChanged);
            }
            else
            {
                SessionChanged(session);
            }

            initialized = true;
        }

        void NotifyTagsChanged()
        {
            var entireFile = new SnapshotSpan(buffer.CurrentSnapshot, 0, buffer.CurrentSnapshot.Length);
            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(entireFile));
        }

        void NotifyTagsChanged(int lineNumber)
        {
            var line = buffer.CurrentSnapshot.GetLineFromLineNumber(lineNumber);
            var span = new SnapshotSpan(buffer.CurrentSnapshot, line.Start, line.Length);
            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(span));
        }

        async void SessionChanged(IPullRequestSession session)
        {
            sessionSubscription?.Dispose();
            this.session = session;

            if (file != null)
            {
                file = null;
                NotifyTagsChanged();
            }

            if (session == null) return;

            relativePath = session.GetRelativePath(fullPath);

            if (relativePath == null) return;

            var snapshot = buffer.CurrentSnapshot;

            if (leftHandSide)
            {
                // If we're tagging the LHS of a diff, then the snapshot will be the base commit
                // (as you'd expect) but that means that the diff will be empty, so get the RHS
                // snapshot from the view for the comparison.
                var projection = view.TextSnapshot as IProjectionSnapshot;
                snapshot = projection?.SourceSnapshots.Count == 2 ? projection.SourceSnapshots[1] : null;
            }

            if (snapshot == null) return;

            var repository = gitService.GetRepository(session.LocalRepository.LocalPath);
            file = await session.GetFile(relativePath, !leftHandSide ? this : null);

            if (file == null) return;

            sessionSubscription = file.WhenAnyValue(x => x.InlineCommentThreads)
                .Subscribe(_ => NotifyTagsChanged());

            NotifyTagsChanged();
        }

        void Buffer_Changed(object sender, TextContentChangedEventArgs e)
        {
            if (file != null)
            {
                var snapshot = buffer.CurrentSnapshot;

                foreach (var thread in file.InlineCommentThreads)
                {
                    ITrackingPoint trackingPoint;

                    if (trackingPoints.TryGetValue(thread, out trackingPoint))
                    {
                        var position = trackingPoint.GetPosition(snapshot);
                        var lineNumber = snapshot.GetLineNumberFromPosition(position);

                        if (lineNumber != thread.LineNumber)
                        {
                            thread.LineNumber = lineNumber;
                            thread.IsStale = true;
                            NotifyTagsChanged(thread.LineNumber);
                        }
                    }
                }

                signalRebuild.OnNext(buffer.CurrentSnapshot);
            }
        }

        byte[] GetContents(ITextSnapshot snapshot)
        {
            var currentText = snapshot.GetText();

            var content = document.Encoding.GetBytes(currentText);

            var preamble = document.Encoding.GetPreamble();
            if (preamble.Length == 0) return content;

            var completeContent = new byte[preamble.Length + content.Length];
            Buffer.BlockCopy(preamble, 0, completeContent, 0, preamble.Length);
            Buffer.BlockCopy(content, 0, completeContent, preamble.Length, content.Length);

            return completeContent;
        }

        async Task Rebuild(ITextSnapshot snapshot)
        {
            if (buffer.CurrentSnapshot == snapshot && session != null)
            {
                await session.UpdateEditorContent(relativePath);

                foreach (var thread in file.InlineCommentThreads)
                {
                    if (thread.LineNumber == -1)
                    {
                        trackingPoints.Remove(thread);
                    }
                }

                if (buffer.CurrentSnapshot == snapshot)
                {
                    NotifyTagsChanged();
                }
            }
        }
    }
}
